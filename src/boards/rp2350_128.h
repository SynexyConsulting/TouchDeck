// Waveshare RP2350-Touch-LCD-1.28: RP2350, round GC9A01A 240x240, CST816S touch.
// Pins from Waveshare's own demo (lib/Config/DEV_Config.h), which match the
// RP2040-Touch-LCD-1.69 wiring. Note LCD reset is GPIO 13: the Pico SDK's
// waveshare_rp2350_touch_lcd_1.28.h says 12, which is the LCD's MISO line.
// No buzzer, no power latch, no RTC on this board.
#pragma once

#define LCD_SPI      spi1
#define LCD_PIN_DC   8
#define LCD_PIN_CS   9
#define LCD_PIN_CLK  10
#define LCD_PIN_MOSI 11
#define LCD_PIN_RST  13
#define LCD_PIN_BL   25

#define I2C_PIN_SDA  6      // touch (CST816S @0x15) and IMU (QMI8658) share I2C1
#define I2C_PIN_SCL  7
#define TP_PIN_INT   21
#define TP_PIN_RST   22
#define IMU_PIN_INT1 23
#define IMU_PIN_INT2 24
#define BAT_ADC_PIN  29

#define LCD_W 240
#define LCD_H 240
