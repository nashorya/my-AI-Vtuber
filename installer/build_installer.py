#!/usr/bin/env python3
"""Build a private Windows installer locally; profiles/payloads never enter CI or Git."""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def nsis_quote(value):
    return str(value).replace('$', '$$').replace('"', '$\\"')


def outside_repo(path):
    for parent in [path, *path.parents]:
        if (parent / '.git').exists():
            raise ValueError('Private profile and output must be outside every Git checkout')


def main():
    os.umask(0o077)
    p = argparse.ArgumentParser()
    p.add_argument('--app-dir', type=Path, required=True)
    p.add_argument('--profile', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--webview-bootstrapper', type=Path, required=True)
    p.add_argument('--version', required=True)
    p.add_argument('--source-commit', required=True)
    p.add_argument('--makensis', default=shutil.which('makensis') or 'makensis')
    p.add_argument('--test-fixture', action='store_true', help='Public dummy profile; output must contain TEST-ONLY')
    a = p.parse_args()
    a.profile, a.output, a.app_dir = (x.resolve() for x in (a.profile, a.output, a.app_dir))
    outside_repo(a.profile)
    outside_repo(a.output)
    if a.output.suffix.lower() != '.exe':
        raise ValueError('Installer output must end in .exe')
    profile = json.loads(a.profile.read_text(encoding='utf-8-sig'))
    if profile.get('account'):
        raise ValueError('Shared installer must not prefill an account')
    if profile.get('profile_id') != 'shared-001':
        raise ValueError('This installer must match the shared-001 invite batch')
    if a.test_fixture:
        if 'TEST-ONLY' not in a.output.name:
            raise ValueError('Dummy installers must be named TEST-ONLY')
        if any(not profile['providers'][k]['api_key'].startswith('FAKE-') for k in ('llm', 'asr', 'tts')):
            raise ValueError('CI test fixture must contain only fake keys')
    else:
        if profile.get('auth_server', '').rstrip('/') != 'https://auth.eatconfusion.online':
            raise ValueError('Production installer must use the deployed auth service')
        for kind in ('llm', 'asr', 'tts'):
            key = profile['providers'][kind].get('api_key', '')
            if len(key) < 8 or any(t in key.lower() for t in ('fake-', 'your-', 'placeholder')):
                raise ValueError(f'{kind} needs a real provider key')
    if not re.fullmatch(r'[0-9a-f]{40}', a.source_commit):
        raise ValueError('source-commit must be the full verified build SHA')
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:[-.][A-Za-z0-9.]+)?', a.version):
        raise ValueError('Invalid version')
    for f in ('AIVTuber.exe', 'WebRtcVad.dll', 'WebUi/wwwroot/streamer.html'):
        if not (a.app_dir / f).is_file():
            raise ValueError('Missing public build file: ' + f)
    if a.webview_bootstrapper.read_bytes()[:2] != b'MZ':
        raise ValueError('WebView2 bootstrapper is not a Windows executable')
    a.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='aivtuber-setup-') as temp:
        root = Path(temp)
        # Reuse the existing secret scan and profile validation, not a parallel packager.
        subprocess.run(['dotnet', 'build', str(REPO / 'tools/AIVTuber.Packager'),
                        '-p:PlatformTarget=AnyCPU', '-t:Rebuild', '--verbosity', 'quiet'],
                       check=True, stdout=subprocess.DEVNULL)
        subprocess.run(['dotnet', str(REPO / 'tools/AIVTuber.Packager/bin/Debug/net10.0/AIVTuber.Packager.dll'),
                        'stream', '--profile', str(a.profile), '--app-dir', str(a.app_dir),
                        '--out', str(root / 'package'), '--app-version', a.version,
                        '--source-commit', a.source_commit], check=True, stdout=subprocess.DEVNULL)
        package = next((root / 'package').glob('*.zip'))
        payload = root / 'payload'
        with zipfile.ZipFile(package) as z:
            z.extractall(payload)
        files = sorted(p for p in payload.rglob('*') if p.is_file())
        # Delete only files we ship. User config, memory, logs and avatars survive uninstall.
        deletes = []
        for f in files:
            target = '$INSTDIR\\' + nsis_quote(str(f.relative_to(payload)).replace('/', '\\'))
            deletes += [f'IfFileExists "{target}" 0 +5', 'ClearErrors',
                        f'Delete "{target}"', 'IfErrors 0 +2', 'StrCpy $9 1']
        dirs = sorted({d for f in files for d in f.relative_to(payload).parents if str(d) != '.'}, key=lambda d: len(d.parts), reverse=True)
        deletes += ['RMDir "$INSTDIR\\' + nsis_quote(str(d).replace('/', '\\')) + '"' for d in dirs]
        (root / 'uninstall-files.nsh').write_text('\n'.join(deletes), encoding='utf-8')
        defines = {'PAYLOAD': payload, 'OUTPUT': a.output, 'APP_VERSION': a.version,
                   'WEBVIEW_BOOTSTRAPPER': a.webview_bootstrapper.resolve(), 'UNINSTALL_FILES': root / 'uninstall-files.nsh',
                   'FILE_VERSION': '.'.join(a.version.split('-')[0].split('.')[:3]) + '.0'}
        # NSIS on Unix uses -D; Windows uses /D. Path values are passed as one argument.
        flag = '/D' if __import__('os').name == 'nt' else '-D'
        subprocess.run([a.makensis, *[flag + k + '=' + str(v) for k, v in defines.items()], str(REPO / 'installer/AIVTuber.nsi')], check=True, stdout=subprocess.DEVNULL)
    digest = hashlib.sha256(a.output.read_bytes()).hexdigest()
    a.output.with_suffix('.exe.sha256').write_text(f'{digest}  {a.output.name}\n')
    receipt = {'installer': a.output.name, 'sha256': digest, 'bytes': a.output.stat().st_size,
               'profile_id': profile['profile_id'], 'auth_server': profile['auth_server'],
               'source_commit': a.source_commit, 'app_version': a.version, 'test_only': a.test_fixture}
    a.output.with_suffix('.receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
