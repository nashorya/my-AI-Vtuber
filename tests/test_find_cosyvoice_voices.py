import json
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

from scripts import find_cosyvoice_voices as finder


class EndpointAndFilteringTests(unittest.TestCase):
    def test_build_endpoint_uses_workspace_or_legacy_domain(self):
        self.assertEqual(
            finder.build_endpoint("beijing", "ws-123"),
            "https://ws-123.cn-beijing.maas.aliyuncs.com/api/v1/services/audio/tts/customization",
        )
        self.assertEqual(
            finder.build_endpoint("singapore", None),
            "https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization",
        )

    def test_morning_filter_includes_midnight_and_excludes_noon(self):
        tz = ZoneInfo("Asia/Shanghai")
        candidates = [
            self.candidate("midnight", datetime(2026, 8, 3, 0, 0, tzinfo=tz)),
            self.candidate("latest", datetime(2026, 8, 3, 11, 59, 59, tzinfo=tz)),
            self.candidate("noon", datetime(2026, 8, 3, 12, 0, tzinfo=tz)),
        ]

        chosen, used_morning = finder.choose_display_candidates(
            candidates,
            datetime(2026, 8, 3, 21, 0, tzinfo=tz),
            show_all=False,
        )

        self.assertTrue(used_morning)
        self.assertEqual([item.voice_id for item in chosen], ["latest", "midnight"])

    def test_no_morning_match_falls_back_to_all_sorted_newest_first(self):
        tz = ZoneInfo("Asia/Shanghai")
        candidates = [
            self.candidate("old", datetime(2026, 8, 2, 8, 0, tzinfo=tz)),
            self.candidate("new", datetime(2026, 8, 3, 15, 0, tzinfo=tz)),
        ]

        chosen, used_morning = finder.choose_display_candidates(
            candidates,
            datetime(2026, 8, 3, 21, 0, tzinfo=tz),
            show_all=False,
        )

        self.assertFalse(used_morning)
        self.assertEqual([item.voice_id for item in chosen], ["new", "old"])

    @staticmethod
    def candidate(voice_id, created):
        return finder.VoiceCandidate(voice_id, created, "OK", "cosyvoice-v3-flash")


class ClientTests(unittest.TestCase):
    def test_get_candidate_queries_exact_voice_id(self):
        def transport(url, api_key, payload, timeout):
            self.assertEqual(payload["input"], {"action": "query_voice", "voice_id": "exact-id"})
            return {
                "output": {
                    "status": "OK",
                    "target_model": "cosyvoice-v3.5-plus",
                    "gmt_create": "2026-08-03 10:34:57",
                }
            }

        client = finder.CosyVoiceClient("https://example.invalid", "secret", transport=transport)
        candidate = client.get_candidate("exact-id")

        self.assertEqual(candidate.voice_id, "exact-id")
        self.assertEqual(candidate.target_model, "cosyvoice-v3.5-plus")

    def test_list_candidates_paginates_deduplicates_and_gets_model(self):
        calls = []

        def transport(url, api_key, payload, timeout):
            calls.append(payload)
            action = payload["input"]["action"]
            if action == "list_voice":
                page = payload["input"]["page_index"]
                pages = {
                    0: [
                        {"voice_id": "ready", "gmt_create": "2026-08-03 09:00:00", "status": "OK"},
                        {"voice_id": "sleeping", "gmt_create": "2026-08-03 08:00:00", "status": "UNDEPLOYED"},
                    ],
                    1: [
                        {"voice_id": "ready", "gmt_create": "2026-08-03 09:00:00", "status": "OK"},
                    ],
                }
                return {"output": {"voice_list": pages[page]}}
            self.assertEqual(payload["input"]["voice_id"], "ready")
            return {
                "output": {
                    "status": "OK",
                    "target_model": "cosyvoice-v3.5-flash",
                    "gmt_create": "2026-08-03 09:00:00",
                }
            }

        client = finder.CosyVoiceClient(
            "https://example.invalid/api", "secret", transport=transport
        )
        result = client.list_candidates(prefix="mine", page_size=2)

        self.assertEqual(len(result), 1)
        self.assertEqual(result[0].voice_id, "ready")
        self.assertEqual(result[0].target_model, "cosyvoice-v3.5-flash")
        list_calls = [c for c in calls if c["input"]["action"] == "list_voice"]
        self.assertEqual([c["input"]["page_index"] for c in list_calls], [0, 1])
        self.assertEqual(list_calls[0]["input"]["prefix"], "mine")

    def test_repeated_full_page_stops(self):
        list_call_count = 0

        def transport(url, api_key, payload, timeout):
            nonlocal list_call_count
            if payload["input"]["action"] == "list_voice":
                list_call_count += 1
                return {
                    "output": {
                        "voice_list": [
                            {"voice_id": "same", "gmt_create": "2026-08-03 07:00:00", "status": "OK"}
                        ]
                    }
                }
            return {"output": {"status": "OK", "target_model": "cosyvoice-v3-flash"}}

        client = finder.CosyVoiceClient("https://example.invalid", "secret", transport=transport)
        result = client.list_candidates(page_size=1, max_pages=50)

        self.assertEqual([v.voice_id for v in result], ["same"])
        self.assertEqual(list_call_count, 2)

    def test_all_detail_queries_failing_is_an_error(self):
        def transport(url, api_key, payload, timeout):
            if payload["input"]["action"] == "list_voice":
                return {
                    "output": {
                        "voice_list": [
                            {"voice_id": "broken", "gmt_create": "2026-08-03 07:00:00", "status": "OK"}
                        ]
                    }
                }
            raise finder.ApiError("detail unavailable")

        client = finder.CosyVoiceClient("https://example.invalid", "secret", transport=transport)
        with self.assertRaisesRegex(finder.ApiError, "无法验证"):
            client.list_candidates(page_size=100)


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir.name)

    def tearDown(self):
        self.temp_dir.cleanup()

    def candidate(self):
        return finder.VoiceCandidate(
            "cosyvoice-v3-flash-mine-123",
            datetime(2026, 8, 3, 9, 0, tzinfo=ZoneInfo("Asia/Shanghai")),
            "OK",
            "cosyvoice-v3-flash",
        )

    def test_update_preserves_unknown_fields_and_creates_backup(self):
        path = self.root / "config.json"
        original = {"tts": {"speed": 1.2, "voice_id": "old"}, "unrelated": {"keep": True}}
        path.write_text(json.dumps(original), encoding="utf-8")

        backup = finder.update_tts_config(path, self.candidate())

        updated = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(updated["tts"]["provider"], "aliyun")
        self.assertEqual(updated["tts"]["voice_id"], self.candidate().voice_id)
        self.assertEqual(updated["tts"]["model"], self.candidate().target_model)
        self.assertEqual(updated["tts"]["speed"], 1.2)
        self.assertEqual(updated["unrelated"], {"keep": True})
        self.assertEqual(json.loads(backup.read_text(encoding="utf-8")), original)

    def test_invalid_tts_shape_does_not_change_file(self):
        path = self.root / "config.json"
        original = '{"tts": "invalid"}'
        path.write_text(original, encoding="utf-8")

        with self.assertRaises(finder.ConfigError):
            finder.update_tts_config(path, self.candidate())

        self.assertEqual(path.read_text(encoding="utf-8"), original)
        self.assertFalse((self.root / "config.json.bak").exists())

    def test_locate_config_prefers_explicit_then_current_directory(self):
        explicit = self.root / "chosen.json"
        explicit.write_text("{}", encoding="utf-8")
        cwd = self.root / "cwd"
        cwd.mkdir()
        (cwd / "config.json").write_text("{}", encoding="utf-8")

        self.assertEqual(finder.locate_config(explicit, cwd=cwd), explicit.resolve())
        self.assertEqual(finder.locate_config(None, cwd=cwd), (cwd / "config.json").resolve())


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.config = Path(self.temp_dir.name) / "config.json"
        self.config.write_text('{"tts":{"speed":1.0}}', encoding="utf-8")
        self.candidates = [
            finder.VoiceCandidate(
                "morning-voice",
                datetime(2026, 8, 3, 9, 0, tzinfo=ZoneInfo("Asia/Shanghai")),
                "OK",
                "cosyvoice-v3.5-flash",
            )
        ]

    def tearDown(self):
        self.temp_dir.cleanup()

    def client_factory(self, **kwargs):
        candidates = self.candidates

        class FakeClient:
            def list_candidates(self, prefix=None):
                return candidates

        return FakeClient()

    def test_missing_api_key_returns_two(self):
        output = []
        code = finder.run(
            ["--config", str(self.config)],
            environ={},
            output_fn=output.append,
            client_factory=self.client_factory,
        )
        self.assertEqual(code, 2)
        self.assertIn("DASHSCOPE_API_KEY", "\n".join(output))

    def test_confirmation_updates_config(self):
        answers = iter(["", "y"])
        output = []
        code = finder.run(
            ["--config", str(self.config)],
            environ={"DASHSCOPE_API_KEY": "secret"},
            input_fn=lambda prompt: next(answers),
            output_fn=output.append,
            client_factory=self.client_factory,
            now=datetime(2026, 8, 3, 20, 0, tzinfo=ZoneInfo("Asia/Shanghai")),
        )

        self.assertEqual(code, 0)
        updated = json.loads(self.config.read_text(encoding="utf-8"))
        self.assertEqual(updated["tts"]["voice_id"], "morning-voice")
        self.assertEqual(updated["tts"]["model"], "cosyvoice-v3.5-flash")
        self.assertTrue(self.config.with_name("config.json.bak").exists())

    def test_cancel_at_confirmation_does_not_write(self):
        before = self.config.read_text(encoding="utf-8")
        answers = iter(["1", "n"])
        code = finder.run(
            ["--config", str(self.config)],
            environ={"DASHSCOPE_API_KEY": "secret"},
            input_fn=lambda prompt: next(answers),
            output_fn=lambda value: None,
            client_factory=self.client_factory,
            now=datetime(2026, 8, 3, 20, 0, tzinfo=ZoneInfo("Asia/Shanghai")),
        )

        self.assertEqual(code, 0)
        self.assertEqual(self.config.read_text(encoding="utf-8"), before)
        self.assertFalse(self.config.with_name("config.json.bak").exists())

    def test_voice_id_queries_exact_candidate_without_listing(self):
        requested = []
        candidate = self.candidates[0]

        def factory(**kwargs):
            class FakeClient:
                def get_candidate(self, voice_id):
                    requested.append(voice_id)
                    return candidate

                def list_candidates(self, prefix=None):
                    raise AssertionError("exact selection must not list voices")

            return FakeClient()

        answers = iter(["", "n"])
        code = finder.run(
            ["--config", str(self.config), "--voice-id", candidate.voice_id],
            environ={"DASHSCOPE_API_KEY": "secret"},
            input_fn=lambda prompt: next(answers),
            output_fn=lambda value: None,
            client_factory=factory,
            now=datetime(2026, 8, 3, 20, 0, tzinfo=ZoneInfo("Asia/Shanghai")),
        )

        self.assertEqual(code, 0)
        self.assertEqual(requested, [candidate.voice_id])


if __name__ == "__main__":
    unittest.main()
