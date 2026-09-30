// Pin map for Waveshare RP2040-Touch-LCD-1.69 (from RP2040-Touch-LCD-1.69-Sch.pdf).
#pragma once
#ifdef TD_BOARD_RP2350_128
#include "boards/rp2350_128.h"   // Waveshare RP2350-Touch-LCD-1.28
#else

#define LCD_SPI      spi1
#define LCD_PIN_DC   8
#define LCD_PIN_CS   9
#define LCD_PIN_CLK  10
#define LCD_PIN_MOSI 11
#define LCD_PIN_RST  13
#define LCD_PIN_BL   25

// Buzzer: GPIO2 -> 10uF -> SS8050 base. AC-coupled, so it only makes sound
// from a switching (PWM) signal; a steady level is silent.
#define BUZZER_PIN   2

// Power latch: SYS_EN high keeps the board powered when running on battery.
#define SYS_EN_PIN   15
#define SYS_OUT_PIN  14   // power key sense

// Shared I2C1 bus: CST816T touch, QMI8658 IMU, PCF85063 RTC.
#define I2C_PIN_SDA  6
#define I2C_PIN_SCL  7
#define TP_PIN_INT   21
#define TP_PIN_RST   22
#define RTC_PIN_INT  18
#define BAT_ADC_PIN  29

#define LCD_W 240
#define LCD_H 280
#endif   // TD_BOARD_RP2350_128
