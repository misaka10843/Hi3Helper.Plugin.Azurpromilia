#!/usr/bin/env python3
"""
Live diagnostic probe for the Azur Promilia launcher API signature scheme.

Goal: determine, against the real server, exactly how `sign` must be constructed
for GET (query) and POST (JSON body) requests, and how a missing `utoken` is
folded into the signed payload.

This reproduces the header set that AzurPromiliaApiClient sends, so that a
`code=0` result here means the C# implementation is correct.
"""

import hashlib
import json
import time
import urllib.error
import urllib.request
import winreg

# --- channel config, identical to AzurPromiliaChannelConfig.Default -----------
APP_ID = "354"
CHANNEL_CODE = "C000"
SECRET_KEY = "20bccb31-fb57-4683-b6d8-6314d43811f2"
LAUNCHER_ID = "a35deadb9f8846c9ad69bc2f4e299b8c"

BASE = "https://api-grp.manjuu.com"
LAUNCHER_VERSION = "0.6.2.2"
USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) AzurPromilia/%s Chrome/120.0.0.0 Safari/537.36" % LAUNCHER_VERSION
)


def md5_hex(text: str) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def device_id() -> str:
    try:
        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Cryptography", 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY
        )
        guid, _ = winreg.QueryValueEx(key, "MachineGuid")
        winreg.CloseKey(key)
    except OSError:
        guid = "unknown-machine-guid"
    return md5_hex(guid)


DEVICE_ID = device_id()


def compute_sign(payload: str) -> str:
    """extApi3: md5(payload + appId + channelCode + secretKey), lowercase hex."""
    return md5_hex(payload + APP_ID + CHANNEL_CODE + SECRET_KEY)


def client_info(trace_id: str = "") -> str:
    return json.dumps(
        {
            "UA": USER_AGENT,
            "device_model": "Launcher",
            "device_os": "Windows 11 Pro Version 23H2",
            "device_os_version": "10.0.22631.4460",
            "device_network": "Ethernet",
            "device_language": "zh",
            "device_timezone": "UTC+08:00",
            "device_country": "CN",
            "app_version": LAUNCHER_VERSION,
            "grp_trace_id": trace_id,
            "spec_device_info": {},
            "device_uuid": "",
            "sub_channel_trace_id": "",
            "init_trace_id": trace_id,
            "previous_trace_id": "",
            "current_trace_id": trace_id,
        },
        separators=(",", ":"),
        ensure_ascii=False,
    )


def base_headers(utoken_value):
    """`utoken_value` mirrors the JS `C.headers.utoken = O || I()` result."""
    headers = {
        "device-id": DEVICE_ID,
        "grp-trace-id": "",
        "grp-app-id": APP_ID,
        "grp-platform": "Launcher",
        "grp-channel-code": CHANNEL_CODE,
        "grp-sub-channel-code": "SC000",
        "sdk-version": LAUNCHER_VERSION,
        "channel-sdk-version": LAUNCHER_VERSION,
        "grp-launcher-version-code": LAUNCHER_VERSION,
        "lang": "zh-Hans",
        "grp-client-info": client_info(),
        "utoken": utoken_value,
        "User-Agent": USER_AGENT,
    }
    return headers


def send(method, url, headers, body=None):
    data = body.encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")
    except Exception as error:  # noqa: BLE001
        return None, "transport error: %r" % (error,)


def summarise(text, limit=220):
    text = " ".join(text.split())
    return text if len(text) <= limit else text[:limit] + "..."


def probe_get(label, payload_suffix, utoken_value):
    """GET /pc-launcher/client/launcher/config?launcherId=..."""
    canonical = "launcherId=" + LAUNCHER_ID
    timestamp = int(time.time() * 1000)
    payload = str(timestamp) + canonical + payload_suffix
    sign = compute_sign(payload)

    headers = base_headers(utoken_value)
    headers["timestamp"] = str(timestamp)
    headers["sign"] = sign

    url = BASE + "/pc-launcher/client/launcher/config?launcherId=" + LAUNCHER_ID
    status, text = send("GET", url, headers)
    print("  [GET ] %-46s http=%-5s %s" % (label, status, summarise(text)))
    return status, text


def probe_post(label, payload_suffix, utoken_value, body, domain):
    """POST /passportv2/client/user/pwdlogin"""
    timestamp = int(time.time() * 1000)
    payload = str(timestamp) + payload_suffix
    sign = compute_sign(payload)

    headers = base_headers(utoken_value)
    headers["timestamp"] = str(timestamp)
    headers["sign"] = sign
    headers["Content-Type"] = "application/json"

    url = domain.rstrip("/") + "/passportv2/client/user/pwdlogin"
    status, text = send("POST", url, headers, body)
    print("  [POST] %-46s http=%-5s %s" % (label, status, summarise(text)))
    return status, text


def fetch_config():
    canonical = "launcherId=" + LAUNCHER_ID
    timestamp = int(time.time() * 1000)
    headers = base_headers("")
    headers["timestamp"] = str(timestamp)
    headers["sign"] = compute_sign(str(timestamp) + canonical + "")

    url = BASE + "/pc-launcher/client/launcher/config?launcherId=" + LAUNCHER_ID
    _, text = send("GET", url, headers)
    return json.loads(text)


def main():
    print("device-id = %s" % DEVICE_ID)
    print()

    print("== A. GET signature: how is a missing utoken folded in? ==")
    probe_get("A1  suffix=''      utoken=''", "", "")
    probe_get("A2  suffix='null'  utoken='null'", "null", "null")
    probe_get("A3  suffix=''      utoken='null'", "", "null")
    print()

    config = fetch_config()
    init_config = config.get("data", {}).get("launcherInitConfig", {})
    user_domains = (init_config.get("user") or {}).get("domains") or []
    compliance_domains = (init_config.get("compliance") or {}).get("domains") or []
    heartbeat_domains = (init_config.get("heartbeat") or {}).get("domains") or []
    print("== server-published domains ==")
    print("  user       = %s" % user_domains)
    print("  compliance = %s" % compliance_domains)
    print("  heartbeat  = %s" % heartbeat_domains)
    print("  pwdKey     = %s" % ((init_config.get("common") or {}).get("encrypt.password.pwd")))
    print()

    post_domain = (user_domains or [BASE])[0]
    print("== B. POST on user domain %s ==" % post_domain)

    # A syntactically valid but non-existent account, so a correct signature is
    # distinguishable from an incorrect one purely by the returned code/msg.
    probe_account = "azurprobe0000@example.com"
    probe_pwd = "A" * 108  # extApi4 output is always 108 chars
    body = json.dumps(
        {"accountType": 2, "account": probe_account, "pwd": probe_pwd},
        separators=(",", ":"),
        ensure_ascii=False,
    )

    print("== B. POST signature variants (body=%s) ==" % summarise(body, 120))
    print("   body length = %d, pwd length = %d" % (len(body), len(probe_pwd)))
    probe_post("B1  CURRENT BUG: suffix=''      utoken=''", "", "", body, post_domain)
    probe_post("B2  DOCUMENTED:  suffix=body    utoken=''", body, "", body, post_domain)
    probe_post("B3  B2 + null:   suffix=body    utoken='null'", body, "null", body, post_domain)
    probe_post("B4  no-body sig + null utoken", "", "null", body, post_domain)
    print()

    print("== C. POST with a deliberately corrupted sign (control) ==")
    timestamp = int(time.time() * 1000)
    headers = base_headers("")
    headers["timestamp"] = str(timestamp)
    headers["sign"] = "0" * 32
    headers["Content-Type"] = "application/json"
    status, text = send("POST", post_domain.rstrip("/") + "/passportv2/client/user/pwdlogin", headers, body)
    print("  [POST] %-46s http=%-5s %s" % ("C1  sign=000...000", status, summarise(text)))


if __name__ == "__main__":
    main()
