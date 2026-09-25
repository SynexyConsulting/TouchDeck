// Pin map for the ESP32-2424S012C (ESP32-C3 + 1.28" round touch display).
#pragma once

#define LCD_PIN_SCLK 6
#define LCD_PIN_MOSI 7
#define LCD_PIN_DC   2
#define LCD_PIN_CS   10
#define LCD_PIN_BL   3

// CST816D touch on I2C
#define TP_PIN_SDA   4
#define TP_PIN_SCL   5
#define TP_PIN_INT   0
#define TP_PIN_RST   1

#define BOOT_BTN_PIN 9

#define LCD_W 240
#define LCD_H 240
