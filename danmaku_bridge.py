"""
Bilibili danmaku bridge script.
Connects to Bilibili live danmaku stream and pushes messages
to the C# AIVTuber backend via local HTTP POST.

Dependencies: pip install bilibili-api-python httpx
All configuration is injected via environment variables by the C# host process.
"""
import os
import asyncio
import httpx
from bilibili_api import Credential
from bilibili_api.live import LiveDanmaku

ROOM_ID  = int(os.environ.get("ROOM_ID", "0"))
SESSDATA = os.environ.get("SESSDATA", "")
BILI_JCT = os.environ.get("BILI_JCT", "")
BUVID3   = os.environ.get("BUVID3", "")
PUSH_URL = os.environ.get("PUSH_URL", "http://localhost:19876/danmaku")


async def main():
    if ROOM_ID == 0:
        print("ERROR: ROOM_ID not set")
        return

    credential = Credential(sessdata=SESSDATA, bili_jct=BILI_JCT, buvid3=BUVID3)
    monitor = LiveDanmaku(ROOM_ID, credential=credential)

    @monitor.on("DANMU_MSG")
    async def on_danmaku(event):
        uid      = str(event["data"]["info"][2][0])
        username = event["data"]["info"][2][1]
        content  = event["data"]["info"][1]
        try:
            async with httpx.AsyncClient(timeout=3) as client:
                await client.post(PUSH_URL, json={
                    "uid": uid,
                    "username": username,
                    "content": content
                })
        except Exception:
            pass  # Backend not ready, ignore silently

    print(f"Connecting to room {ROOM_ID}...")
    await monitor.connect()


if __name__ == "__main__":
    asyncio.run(main())