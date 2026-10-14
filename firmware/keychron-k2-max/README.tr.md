# Keychron K2 Max — 2.4 GHz pil firmware yaması

[English](README.md) · **Türkçe**

Keychron'un stok firmware'i pil yüzdesini 2.4 GHz alıcı üzerinden bilgisayara iletmez.
Bu yama, klavyenin pil durumunu **5 saniyede bir kendiliğinden** raw HID kanalından
(usage page `0xFF60`) göndermesini sağlar. BatteryTray bu kanalı dinler.

Kablo (Cable) modunda ise uygulama `0xA4` komutuyla pili doğrudan sorar.

## Paket biçimi

```
[0] 0xA4   (KC_GET_BATTERY)
[1] yüzde (0–100)
[2] voltaj mV, düşük bayt
[3] voltaj mV, yüksek bayt
[4] şarj durumu: 0 = pilde, 1 = şarj oluyor, 2 = dolu
[5] bağlantı: 0x01 USB, 0x02 Bluetooth, 0x04 2.4 GHz
```

## Değişiklikler

- `keychron_raw_hid.c/.h`: `KC_GET_BATTERY (0xA4)` komutu ve `kc_battery_push()`
- `wireless/battery.c/.h`: `battery_get_charging_state()`; Cable modunda da voltaj ölçümü
- `k2_max/k2_max.c`: 2.4 GHz'deyken 5 sn'de bir pil paketi gönderen `keychron_task_kb()`

Tuş davranışına, ışıklara ve Bluetooth moduna dokunmaz.

## Derleme

Taban: [Keychron/qmk_firmware](https://github.com/Keychron/qmk_firmware), `2025q3` dalı,
commit `c5d998442710b38650903937249144a0cd728ebe`.

```bash
git clone -b 2025q3 https://github.com/Keychron/qmk_firmware.git
cd qmk_firmware
git apply /yol/k2max-battery-push.patch
make keychron/k2_max/iso/rgb:keychron      # ya da ansi/jis, rgb/white
```

Hazır `.bin` dosyaları reponun **Releases** sayfasında.

## Yükleme

1. [QMK Toolbox](https://github.com/qmk/qmk_toolbox/releases)'u kur, `.bin` dosyasını aç.
2. Kabloyu çıkar, yan anahtarı **Cable**'a getir, **Esc**'ye basılıyken kabloyu tak.
3. `STM32 DFU device connected` görünce **Flash**.

Geri dönmek için [Keychron Launcher](https://launcher.keychron.com/) üzerinden resmi
firmware'i yükleyebilirsin. İşlemcinin yükleme modu ROM'da olduğu için klavye kalıcı
olarak bozulmaz.

## Kaynak ve lisans

Yama, [OutersGIT/k10he-battery-firmware](https://github.com/OutersGIT/k10he-battery-firmware)
projesinin K10 HE için yaptığı değişikliklerin K2 Max'e uyarlanmış halidir.
QMK Firmware türevi olduğu için **GPL-2.0-or-later** lisanslıdır.
