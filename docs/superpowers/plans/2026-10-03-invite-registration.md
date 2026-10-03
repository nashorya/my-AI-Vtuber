# Invite Registration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Operators generate one-time invite codes; streamers register in the login page with invite code + username + password using one shared installer; accounts created this way never expire.

**Architecture:** A new `invites` table and a transactional `AuthService.Register` on the account service (same reply shape as login so the daily-quota flow is untouched); `CloudLicense.RegisterAsync` shares its sign-in path with login; the WPF login page gains a register mode; `DistributionProfile.Account` becomes optional for the shared package.

**Tech Stack:** C# / .NET 10, ASP.NET Core Minimal API + SQLite, xUnit, WPF (compile-verified only on macOS).

**Spec:** `docs/superpowers/specs/2026-10-03-invite-registration-design.md`

## Global Constraints

- Invite code: 8 chars from `ABCDEFGHJKMNPQRSTUVWXYZ23456789`, shown as `XXXX-XXXX`; input ignores case, `-` and spaces; stored normalized (upper, no dash).
- No-expiry sentinel: `AuthService.NoExpiry = 2100-01-01T00:00:00Z`; `valid_until >= NoExpiry` means "长期有效". Never use 9999 (monotonic-tick overflow on the client).
- Username 3–20 chars `[A-Za-z0-9_-]`; password ≥ 8 chars.
- `invalid_invite` covers not-found / already-used / revoked / profile-mismatch with one status and one message; only it counts toward the registration rate limit (5 per 15 min per source).
- Source key for rate limiting: `X-Real-IP` when the connection is loopback, else the remote address.
- Registration and account insert + invite consumption happen in ONE database transaction; a username conflict rolls back and leaves the invite unused.
- Wire status names (snake_case): `invalid_invite` 403, `username_taken` 409, `invalid_username` 400, `weak_password` 400.
- Chinese copy exactly: `邀请码不对或已经用过，请向发放者确认` / `这个账号名已被占用，换一个试试` / `账号名需要 3 到 20 位，只能用字母、数字、下划线和横线` / `密码至少需要 8 位` / `两次输入的密码不一致` / `长期有效`.
- Test runs on macOS: append `-p:PlatformTarget=AnyCPU` to every `dotnet test`; revert `packages.lock.json` changes before committing. Known unrelated failures: `StreamerConfigTests.StreamerDraft_ContainsNoManagedServiceFields`, `DashScopeConnectionPoolTests.GetOrCreateAsync_InvalidEndpoint_InvokesOnErrorAndThrows` (macOS only).
- Keep the old per-streamer flow (`create`, dedicated package) working.

## Review Focus

- Two registrations racing on the same code: exactly one wins. → Task 1 `Register_SameCodeTwice_OnlyOneAccount`.
- Username taken must not burn the invite. → Task 1 `Register_UsernameTaken_KeepsTheInviteUsable`.
- Codes typed with lowercase / dashes / spaces still work. → Task 1 `NormalizeInvite_*`.
- Behind nginx every request comes from 127.0.0.1: the rate limit must key on `X-Real-IP`. → Task 1 `SourceKey_UsesRealIpBehindLoopback`.
- A no-expiry account must still get a normal lease and quota. → Task 1 `Register_NoExpiryAccount_GetsNormalLeaseAndQuota`.
- Mismatched password confirmation must not hit the network. → Task 5 `Register_PasswordMismatch_DoesNotCallTheServer`.

---

### Task 1: Server — invites, registration, no-expiry

**Files:** Modify `AIVTuber.AuthServer/AuthStore.cs`, `AuthModels.cs`, `AuthService.cs`, `AuthServerApp.cs`; Test `AIVTuber.Tests/Auth/AuthServiceRegisterTests.cs` (create)

**Interfaces — produces:**
- `AuthStatus` += `InvalidInvite`, `UsernameTaken`, `InvalidUsername`, `WeakPassword` (wire `invalid_invite`, `username_taken`, `invalid_username`, `weak_password`).
- `record RegisterRequest(string InviteCode, string Username, string Password, string ProfileId, string AppVersion, int CredentialRevision)`.
- `AuthService.NoExpiry` (`DateTimeOffset`), `AuthService.NormalizeInvite(string) -> string`, `AuthService.FormatInvite(string) -> string`, `AuthService.CreateInvites(int count, string profileId, string note) -> IReadOnlyList<string>` (formatted codes), `AuthService.Register(RegisterRequest, string sourceKey) -> AuthResult`, `AuthService.RevokeInvite(string code)`, `AuthService.ListInvites() -> IReadOnlyList<InviteRow>`.
- `record InviteRow(string Code, string ProfileId, string Note, DateTimeOffset CreatedAt, string? UsedByAccountId, string? UsedByUsername, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt)`.
- `AuthStore`: `bool InsertInvite(string code, string profileId, string note, DateTimeOffset now)` (false on duplicate), `RegisterOutcome RegisterWithInvite(string code, AccountRow account, DateTimeOffset now)`, `bool RevokeInvite(string code, DateTimeOffset now)`, `IReadOnlyList<InviteRow> ListInvites()`; `enum RegisterOutcome { Ok, InvalidInvite, UsernameTaken }`.
- Static `AuthServerApp.SourceKey(IPAddress? remote, string? realIpHeader) -> string`.

- [ ] **Step 1: Write failing tests** in `AuthServiceRegisterTests.cs` (same fixture pattern as `AuthServiceQuotaTests`: temp db, `ManualClock`, `AuthStore.Open`, `AuthService`). Tests:
  - `NormalizeInvite_IgnoresCaseDashAndSpaces`: `" k7m4-9qxd "` → `"K7M49QXD"`; `FormatInvite("K7M49QXD")` → `"K7M4-9QXD"`.
  - `CreateInvites_ReturnsDistinctFormattedCodes`: 50 codes, all match `^[A-HJ-KM-NP-Z2-9]{4}-[A-HJ-KM-NP-Z2-9]{4}$`, all distinct.
  - `Register_Success_CreatesAccountMarksInviteAndLogsIn`: status `Ok`, token present, `QuotaSeconds == 3600`, `FindAccountByUsername("alice")` not null with `ProfileId == "shared-001"`, invite listed as used by alice.
  - `Register_NoExpiryAccount_GetsNormalLeaseAndQuota`: `AccountValidUntil == AuthService.NoExpiry`, `LeaseValidUntil == ServerTime + 180 s`, then a heartbeat after 60 s with `active_seconds 55` returns remaining 3545.
  - `Register_SameCodeTwice_OnlyOneAccount`: second register with a different username → `InvalidInvite`; only one account exists.
  - `Register_UsernameTaken_KeepsTheInviteUsable`: existing "alice"; register with code + "alice" → `UsernameTaken`; register same code + "bob" → `Ok`.
  - `Register_BadInvites_AllLookTheSame`: unknown code, revoked code, code for another profile → all `InvalidInvite`.
  - `Register_ValidatesUsernameAndPassword`: `"ab"`, `"a b"`, 21 chars, `"名字名字名"` → `InvalidUsername`; password `"1234567"` → `WeakPassword`; neither consumes the invite.
  - `Register_InvalidInviteIsRateLimited`: 5 bad codes from source `"1.2.3.4"` → 6th attempt (even with a good code) → `RateLimited`; a different source is unaffected; username/password errors do not count.
  - `Register_CodeTypedWithLowercaseAndDash_Works`.
  - `RevokeInvite_BlocksUse_AndUsedInviteCannotBeRevoked`.
  - `SourceKey_UsesRealIpBehindLoopback`: loopback + header `"9.9.9.9"` → `"9.9.9.9"`; non-loopback remote ignores the header; missing header → remote address.

- [ ] **Step 2: Run to verify failure** — `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -p:PlatformTarget=AnyCPU --filter FullyQualifiedName~AuthServiceRegisterTests`; Expected: compile errors (new members missing).

- [ ] **Step 3: Implement.**
  - `AuthStore.Open`: add `CREATE TABLE IF NOT EXISTS invites (...)` per spec.
  - `InviteRow`/`RegisterOutcome` types; store methods. `RegisterWithInvite` runs under `_sync` in one `BeginTransaction()`: `SELECT profile_id, used_by_account_id, revoked_at FROM invites WHERE code=$c` → missing/used/revoked or `profile_id != account.ProfileId` → `InvalidInvite` (roll back); insert account (catch `SqliteException` code 19 → rollback, `UsernameTaken`); `UPDATE invites SET used_by_account_id=$a, used_at=$n WHERE code=$c AND used_by_account_id IS NULL` and require 1 row else rollback + `InvalidInvite`; commit → `Ok`. `ListInvites` left-joins `accounts` for the username. `RevokeInvite`: `UPDATE ... SET revoked_at=$n WHERE code=$c AND used_by_account_id IS NULL AND revoked_at IS NULL` → returns rows == 1.
  - `AuthStatus` enum + wire names; `AuthServerApp.Respond` maps `InvalidInvite` → 403, `UsernameTaken` → 409, `InvalidUsername`/`WeakPassword` → 400.
  - `AuthService`: constants/regexes; `NormalizeInvite` = `new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant()`; `FormatInvite` inserts `-` after 4; `CreateInvites` generates with `RandomNumberGenerator.GetInt32(alphabet.Length)` and retries on duplicate; `Register` per spec order using a separate failure window map key `"reg:" + sourceKey`; on success `store.InsertSession(...)` then `Granted(account, now) with { SessionToken = token }` exactly like `Login`; validity of username `^[A-Za-z0-9_-]{3,20}$`.
  - `AuthServerApp`: `app.MapPost("/v1/auth/register", (RegisterRequest r, HttpContext http, AuthService auth) => …)` logging status/account/profile only (never the code or password); `public static string SourceKey(IPAddress? remote, string? realIp)`.

- [ ] **Step 4: Run to verify pass** — same command plus `FullyQualifiedName~Auth`; Expected: PASS.

- [ ] **Step 5: Commit** — `git add AIVTuber.AuthServer AIVTuber.Tests/Auth/AuthServiceRegisterTests.cs && git commit -m "Auth server: invite codes and one-time registration"`.

---

### Task 2: Server — admin commands

**Files:** Modify `AIVTuber.AuthServer/AdminCli.cs`; Test append to `AIVTuber.Tests/Auth/AdminCliTests.cs`

**Interfaces — consumes:** Task 1 `CreateInvites/ListInvites/RevokeInvite/NoExpiry`. **Produces:** commands `invite create --count N --profile P [--note T]`, `invite list`, `invite revoke --code C`; `create … --no-expiry`.

- [ ] **Step 1: Failing tests** (AdminCliTests style, `Run(stdin, args…)`): `InviteCreate_PrintsFormattedCodes` (`--count 3 --profile shared-001 --note 内测` → exit 0, 3 lines matching the code pattern); `InviteCreate_RejectsCountOutOfRange` (0 and 501 → exit 1); `InviteList_ShowsUnusedUsedAndRevoked` (create 3, register one through `AuthService.Register` with the shared db, revoke one → list shows `未用`/`已用 alice`/`已作废`); `InviteRevoke_UnknownOrUsedCodeFails` (exit 1); `Create_NoExpiry_AccountNeverExpires` (login reply `AccountValidUntil == AuthService.NoExpiry`); `Create_NoExpiry_ConflictsWithDays` (exit 1).
- [ ] **Step 2: Run to verify failure** — filter `AdminCliTests`; Expected: new tests FAIL (unknown command / flag).
- [ ] **Step 3: Implement.** Parse: `invite` is a two-word command — treat the first positional as `invite` and the second as the sub-command (extend `Parse` to allow a second positional only for `invite`); `--no-expiry` is a bare flag like `--default`. `invite create`: `--count` 1–500 else `ArgumentException`; print one formatted code per line and nothing else. `invite list`: `code\tprofile=\tstatus\tnote` with status `未用` / `已用 <username> <time>` / `已作废`. `ResolveValidUntil` returns `NoExpiry` for `--no-expiry` and throws if combined with `--days`/`--valid-until`. Update `Usage`.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — "Auth server: admin commands for invite codes and no-expiry accounts".

---

### Task 3: Client — contracts, API client, CloudLicense.RegisterAsync

**Files:** Modify `AIVTuber.Core/Auth/AuthContracts.cs`, `AuthApiClient.cs`, `CloudLicense.cs`; Test `AIVTuber.Tests/Auth/CloudLicenseTests.cs` (append + `FakeAuthApi.Register`), `AuthEndToEndTests.cs`

**Interfaces — produces:** `AuthCode` += `InvalidInvite, UsernameTaken, InvalidUsername, WeakPassword`; `record AuthRegisterRequest(string InviteCode, string Username, string Password, string ProfileId, string AppVersion, int CredentialRevision)`; `IAuthApi.RegisterAsync(AuthRegisterRequest request, CancellationToken ct = default)`; `CloudLicense.RegisterAsync(string inviteCode, string username, string password, CancellationToken ct = default) -> Task<LoginOutcome>`; `CloudLicense.NoExpiryFrom` constant `2100-01-01Z` helper `static bool IsNoExpiry(DateTimeOffset)`.

- [ ] **Step 1: Failing tests:**
  - CloudLicenseTests: `Register_Success_BehavesLikeLogin` (fake `Register` returns `OkWithQuota` → `Success`, `IsAllowed`, epoch 1, `_api.LastRegister` has invite/username/profile/revision); `Register_Denied_ShowsPlainChinese` theory over the four new codes + the exact copy from Global Constraints and `IsAllowed == false`; `IsNoExpiry_Boundary` (2100-01-01 → true, 2099-12-31 → false).
  - AuthEndToEndTests (real service + client): create an invite through `Service.CreateInvites(1,"streamer-017","t")`, `license.RegisterAsync(code,"newbie","password-1")` → success, `QuotaManaged`, then `HeartbeatOnceAsync()` keeps `IsAllowed`; second registration with the same code → failure message `邀请码不对或已经用过，请向发放者确认`; username taken message.
- [ ] **Step 2: Run to verify failure** — compile errors.
- [ ] **Step 3: Implement.** `AuthApiClient.RegisterAsync` → `SendAsync("v1/auth/register", request, null, ct)`; `ParseStatus` new names; `CloudLicense`: extract the body of `LoginAsync` into `private Task<LoginOutcome> SignInAsync(string username, Func<CancellationToken, Task<AuthReply>> call, CancellationToken ct)` (generation bump, `sentAt`, transport-failure handling, state transitions identical to today); `LoginAsync` and `RegisterAsync` call it; `Describe` gains the four messages. `FakeAuthApi` gets `Register` func + `LastRegister`.
- [ ] **Step 4: Run to verify pass** — filter `CloudLicense|AuthEndToEnd|AccountViewModel|StreamerConsole`.
- [ ] **Step 5: Commit** — "Client: register with an invite code through the account service".

---

### Task 4: Optional account in the profile

**Files:** Modify `AIVTuber.Core/Auth/DistributionProfile.cs`, `docs/auth/profile.example.json`; Test `AIVTuber.Tests/Auth/DistributionProfileTests.cs`, `PackagerTests.cs`

- [ ] **Step 1: Failing tests:** `Profile_WithoutAccount_IsValid` (remove `"account"` from the sample JSON → loads, `Account == ""`); `Packager_SharedProfile_BuildsPackageNamedByProfileId` (profile without account → zip name contains `shared-001`, manifest `account` is `""`); keep the existing "missing field rejected" tests for the other fields.
- [ ] **Step 2: Run to verify failure** (`Require(Account, "account")` throws).
- [ ] **Step 3: Implement** — drop `Require(Account, "account")`; example profile documents `account` as optional (comment in README, keep JSON valid).
- [ ] **Step 4: Run to verify pass** — filter `DistributionProfile|Packager`.
- [ ] **Step 5: Commit** — "Profile: account is optional so one package can serve every streamer".

---

### Task 5: AccountViewModel — register mode and no-expiry display

**Files:** Modify `AIVTuber.Core/ViewModels/AccountViewModel.cs`; Test `AIVTuber.Tests/Auth/AccountViewModelTests.cs`

**Interfaces — produces:** `bool IsRegistering`, `void ToggleRegister()`, `Task RegisterAsync(string inviteCode, string password, string confirmPassword)` (username comes from `Username`), `ValidUntilText` shows `长期有效` when `CloudLicense.IsNoExpiry`.

- [ ] **Step 1: Failing tests:** `Register_PasswordMismatch_DoesNotCallTheServer` (error `两次输入的密码不一致`, `_api.LastRegister == null`); `Register_ShortPassword_AndBadUsername_FailLocally` (messages from constraints); `Register_Success_SignsInAndClearsRegisterMode`; `Register_ServerDenial_ShowsMessageAndStaysInRegisterMode`; `ToggleRegister_FlipsModeAndClearsError`; `NoExpiryAccount_ShowsLongTermValidity` (reply `AccountValidUntil = 2100-01-01` → `ValidUntilText == "长期有效"`), dated account still `有效至 …`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** — local validation mirrors the server rules (same regex/length); `RegisterAsync` guards `IsBusy`, calls `_license.RegisterAsync(inviteCode.Trim(), Username.Trim(), password)`; on success `IsRegistering = false`; `Apply` uses `CloudLicense.IsNoExpiry`.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — "AccountViewModel: invite-code registration mode".

---

### Task 6: WPF login page register mode

**Files:** Modify `App/Views/LoginView.xaml`, `App/Views/LoginView.xaml.cs`

- [ ] **Step 1:** Add, inside the existing glass card, a second `StackPanel x:Name="RegisterPanel"` (collapsed by default) with: 邀请码 `TextBox x:Name="InviteBox"`, 账号名 `TextBox x:Name="RegUsernameBox"` bound two-way to `Username`, 密码 `PasswordBox x:Name="RegPasswordInput"`, 再输一次密码 `PasswordBox x:Name="RegConfirmInput"`, error `TextBlock` bound to `ErrorText`, primary button `RegisterButton` 「注册并登录」, and a link-style button 「已有账号？去登录」. Wrap the current login controls in `LoginPanel` and add a link-style button 「没有账号？用邀请码注册」 below its buttons. All new controls use the existing `StreamerTextBox/StreamerPasswordBox/StreamerPrimaryButton/StreamerGlassButton` styles and set `AutomationProperties.Name`.
- [ ] **Step 2:** Code-behind: `OnRegister` (disable button, `await Vm.RegisterAsync(InviteBox.Text, RegPasswordInput.Password, RegConfirmInput.Password)`, clear both password boxes in `finally`), `OnToggleRegister` → `Vm.ToggleRegister()`; subscribe to `Vm.PropertyChanged` (on `DataContextChanged`) and set `LoginPanel`/`RegisterPanel` `Visibility` from `Vm.IsRegistering`; Enter in the confirm box submits; `FocusFirstField` focuses `InviteBox` when registering.
- [ ] **Step 3: Verify** — `xmllint --noout App/Views/LoginView.xaml`; `dotnet build App/App.csproj -c Release -p:EnableWindowsTargeting=true` → 0 errors. (Visual check requires Windows.)
- [ ] **Step 4: Commit** — "Login page: register with an invite code".

---

### Task 7: Docs, full verification

**Files:** Modify `docs/auth/README.md`, `docs/auth/deploy-tencent-server.md`

- [ ] **Step 1:** README: a「邀请码注册与共用安装包」section (flow, `invite` commands, `create --no-expiry`, shared profile + `profile_id` meaning, the shared-key risk and `set-min-revision` to retire a package, registration protocol and the four new status codes in the table). Deployment doc: note that upgrades add the `invites` table automatically.
- [ ] **Step 2:** `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj -p:PlatformTarget=AnyCPU` → only the two known macOS failures; `dotnet build App/App.csproj -c Release -p:EnableWindowsTargeting=true` → 0 errors; `git diff --check`.
- [ ] **Step 3: Commit** — "Docs: invite registration and the shared package".
