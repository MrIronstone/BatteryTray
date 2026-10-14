# Keychron K2 Max — 2.4 GHz battery firmware patch

**English** · [Türkçe](README.tr.md)

Keychron's stock firmware doesn't forward the battery level to the PC over the 2.4 GHz
receiver. This patch makes the keyboard **push its battery state every 5 seconds** on the
raw HID channel (usage page `0xFF60`). BatteryTray listens on that channel.

In Cable mode the app asks for the battery directly with the `0xA4` command.

## Packet format

```
[0] 0xA4   (KC_GET_BATTERY)
[1] percentage (0–100)
[2] voltage mV, low byte
[3] voltage mV, high byte
[4] charge state: 0 = on battery, 1 = charging, 2 = full
[5] transport: 0x01 USB, 0x02 Bluetooth, 0x04 2.4 GHz
```

## Changes

- `keychron_raw_hid.c/.h`: `KC_GET_BATTERY (0xA4)` command and `kc_battery_push()`
- `wireless/battery.c/.h`: `battery_get_charging_state()`; voltage sampling in Cable mode too
- `k2_max/k2_max.c`: `keychron_task_kb()` that sends a battery packet every 5 s while on 2.4 GHz

Key behaviour, lighting and Bluetooth mode are untouched.

## Building

Base: [Keychron/qmk_firmware](https://github.com/Keychron/qmk_firmware), branch `2025q3`,
commit `c5d998442710b38650903937249144a0cd728ebe`.

```bash
git clone -b 2025q3 https://github.com/Keychron/qmk_firmware.git
cd qmk_firmware
git apply /path/to/k2max-battery-push.patch
make keychron/k2_max/iso/rgb:keychron      # or ansi/jis, rgb/white
```

Prebuilt `.bin` files are on the repo's **Releases** page (ISO layout).

## Flashing

1. Install [QMK Toolbox](https://github.com/qmk/qmk_toolbox/releases) and open the `.bin` file.
2. Unplug the cable, set the side switch to **Cable**, hold **Esc** while plugging the cable back in.
3. When `STM32 DFU device connected` appears, click **Flash**.

To go back, flash the official firmware with [Keychron Launcher](https://launcher.keychron.com/).
The MCU's bootloader lives in ROM, so the keyboard can't be permanently bricked.

## Source and license

This patch is a port to the K2 Max of the K10 HE changes from
[OutersGIT/k10he-battery-firmware](https://github.com/OutersGIT/k10he-battery-firmware).
As a QMK Firmware derivative it is licensed **GPL-2.0-or-later**.
