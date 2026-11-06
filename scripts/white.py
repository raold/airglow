# /// script
# requires-python = ">=3.10,<3.15"
# dependencies = ["openrgb-python>=0.3.8", "hidapi>=0.14"]
# ///
"""Set every RGB device in this PC to static white.

OpenRGB (SDK server) drives the DIMMs, GPU, motherboard ITE controllers and the
Lian Li hub; the Keychron and DOIO boards are set over VIA's raw HID protocol.
"""
import argparse
import ctypes
import socket
import subprocess
import sys
import time

import hid
from openrgb import OpenRGBClient
from openrgb.utils import ModeColors, ModeFlags, RGBColor

OPENRGB_EXE = r"C:\Program Files\OpenRGB\OpenRGB.exe"
SDK_HOST, SDK_PORT = "127.0.0.1", 6742
WHITE = RGBColor(255, 255, 255)

# Stream Deck keys are LCDs (white would wipe the icons); the G603 has no RGB.
SKIP_DEVICES = ("stream deck", "g603")
MODE_PREFERENCE = ("static", "static color", "direct")
MANUAL_SAVE = 1 << 8
# Most LEDs one UNI HUB AL channel drives (4 fans x 20). OpenRGB's saved sizes are 1/1/10/1,
# which would leave fans dark; oversizing is harmless.
LIAN_LI_CHANNEL_LEDS = 80

VIA_KEYBOARDS = {(0x3434, 0x0860): "Keychron Q6 Max", (0xD010, 0x1601): "DOIO"}
VIA_USAGE_PAGE, VIA_USAGE = 0xFF60, 0x61
VIA_GET_PROTOCOL, VIA_SET_VALUE, VIA_GET_VALUE, VIA_SAVE = 0x01, 0x07, 0x08, 0x09
VIA_CHANNELS = {3: "RGB matrix", 2: "RGB light"}
VIA_BRIGHTNESS, VIA_EFFECT, VIA_COLOR = 0x01, 0x02, 0x04
VIA_EFFECT_SOLID = 0x01


def sdk_listening():
    try:
        socket.create_connection((SDK_HOST, SDK_PORT), timeout=1).close()
        return True
    except OSError:
        return False


def openrgb_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OpenRGB.exe", "/NH"],
                         capture_output=True, text=True).stdout
    return "OpenRGB.exe" in out


def connect_openrgb():
    if not sdk_listening():
        if openrgb_running():
            sys.exit("OpenRGB is running without its SDK server. Start the server in its "
                     "SDK Server tab, or close OpenRGB, then rerun.")
        print("Starting OpenRGB SDK server (approve the UAC prompt)...")
        rc = ctypes.windll.shell32.ShellExecuteW(
            None, "runas", OPENRGB_EXE, "--server --noautoconnect --startminimized", None, 0)
        if rc <= 32:
            sys.exit(f"Could not start OpenRGB (ShellExecute returned {rc}).")

    deadline = time.monotonic() + 90
    client = None
    while client is None:
        try:
            client = OpenRGBClient(SDK_HOST, SDK_PORT, name="myrgb white")
        except OSError:
            if time.monotonic() > deadline:
                sys.exit("OpenRGB SDK server did not come up.")
            time.sleep(1)

    # Detection keeps registering devices after the server starts listening.
    last, stable = -1, 0
    while stable < 3 and time.monotonic() < deadline:
        time.sleep(2)
        client.update()
        count = len(client.devices)
        stable = stable + 1 if count and count == last else 0
        last = count
    return client


def pick_mode(dev):
    by_name = {m.name.lower(): m for m in dev.modes}
    return next((by_name[n] for n in MODE_PREFERENCE if n in by_name), None)


def apply_openrgb(dry_run):
    client = connect_openrgb()
    for dev in client.devices:
        label = f"{dev.name} [{dev.metadata.location}]"
        if any(s in dev.name.lower() for s in SKIP_DEVICES):
            print(f"skip   {label}")
            continue
        mode = pick_mode(dev)
        if mode is None:
            print(f"FAIL   {label}: no static/direct mode ({', '.join(m.name for m in dev.modes)})")
            continue
        print(f"{'would' if dry_run else 'white'}  {label} via {mode.name!r}")
        if dry_run:
            continue

        if "lian li" in dev.name.lower():
            for zone in dev.zones:
                if len(zone.leds) != LIAN_LI_CHANNEL_LEDS:
                    zone.resize(LIAN_LI_CHANNEL_LEDS)

        flags = int(mode.flags)
        if flags & ModeFlags.HAS_BRIGHTNESS and mode.brightness is not None:
            mode.brightness = mode.brightness_max
        per_led = False
        if flags & ModeFlags.HAS_MODE_SPECIFIC_COLOR:
            mode.color_mode = ModeColors.MODE_SPECIFIC
            mode.colors = [WHITE] * (len(mode.colors or ()) or max(mode.colors_min or 0, 1))
        elif flags & ModeFlags.HAS_PER_LED_COLOR:
            mode.color_mode = ModeColors.PER_LED
            per_led = True

        dev.set_mode(mode)
        if per_led:
            dev.set_color(WHITE)
        if flags & MANUAL_SAVE:
            dev.save_mode()


def via_send(dev, *payload):
    """Send one 32-byte VIA command; return the reply, or None if unhandled or silent."""
    dev.write(bytes([0, *payload]).ljust(33, b"\0"))
    reply = bytes(dev.read(32, 500))
    return reply if reply[:1] == bytes([payload[0]]) else None


def set_via_white(dev, name, dry_run):
    reply = via_send(dev, VIA_GET_PROTOCOL)
    if reply is None:
        print(f"FAIL   {name}: no VIA reply")
        return
    version = reply[1] << 8 | reply[2]
    if version < 11:
        print(f"FAIL   {name}: VIA protocol {version} predates lighting channels")
        return
    for channel, kind in VIA_CHANNELS.items():
        current = via_send(dev, VIA_GET_VALUE, channel, VIA_EFFECT)
        if current is not None:
            break
    else:
        print(f"FAIL   {name}: firmware exposes no RGB over VIA")
        return

    print(f"{'would' if dry_run else 'white'}  {name} via VIA v{version} {kind} "
          f"(current effect {current[3]})")
    if dry_run:
        return
    for value in ((VIA_EFFECT, VIA_EFFECT_SOLID), (VIA_COLOR, 0, 0), (VIA_BRIGHTNESS, 255)):
        if via_send(dev, VIA_SET_VALUE, channel, *value) is None:
            print(f"FAIL   {name}: keyboard rejected value {value[0]:#04x}")
            return
    via_send(dev, VIA_SAVE, channel)


def apply_via(dry_run):
    found = set()
    for info in hid.enumerate():
        name = VIA_KEYBOARDS.get((info["vendor_id"], info["product_id"]))
        if not name or (info["usage_page"], info["usage"]) != (VIA_USAGE_PAGE, VIA_USAGE):
            continue
        found.add(name)
        dev = hid.device()
        dev.open_path(info["path"])
        try:
            set_via_white(dev, name, dry_run)
        finally:
            dev.close()
    for name in sorted(set(VIA_KEYBOARDS.values()) - found):
        print(f"FAIL   {name}: not connected")


def main():
    parser = argparse.ArgumentParser(description="Set every RGB device to static white.")
    parser.add_argument("--dry-run", action="store_true", help="show what would change, write nothing")
    args = parser.parse_args()
    apply_openrgb(args.dry_run)
    apply_via(args.dry_run)


if __name__ == "__main__":
    main()
