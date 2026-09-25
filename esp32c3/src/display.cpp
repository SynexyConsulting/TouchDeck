// GC9A01 via LovyanGFX. Config from LovyanGFX discussion #732 for this board.
// We only use LovyanGFX to drive the panel; drawing happens in our own
// framebuffer (gfx.c) so the look matches the RP2040 Touch Deck.
#define LGFX_USE_V1
#include <LovyanGFX.hpp>
#include "esp_heap_caps.h"
#include "board.h"
#include "display.h"
#include "gfx.h"

class LGFX : public lgfx::LGFX_Device {
    lgfx::Panel_GC9A01 panel;
    lgfx::Bus_SPI bus;
    lgfx::Light_PWM light;

public:
    LGFX() {
        auto b = bus.config();
        b.spi_host = SPI2_HOST;
        b.spi_mode = 0;
        b.freq_write = 80000000;
        b.freq_read = 20000000;
        b.spi_3wire = false;
        b.use_lock = true;
        b.dma_channel = SPI_DMA_CH_AUTO;
        b.pin_sclk = LCD_PIN_SCLK;
        b.pin_mosi = LCD_PIN_MOSI;
        b.pin_miso = -1;
        b.pin_dc = LCD_PIN_DC;
        bus.config(b);
        panel.setBus(&bus);

        auto p = panel.config();
        p.pin_cs = LCD_PIN_CS;
        p.pin_rst = -1;
        p.pin_busy = -1;
        p.panel_width = LCD_W;
        p.panel_height = LCD_H;
        p.readable = false;
        p.invert = true;
        p.rgb_order = false;
        p.bus_shared = false;
        panel.config(p);

        auto l = light.config();
        l.pin_bl = LCD_PIN_BL;
        l.invert = false;
        l.freq = 12000;
        l.pwm_channel = 1;
        light.config(l);
        panel.setLight(&light);

        setPanel(&panel);
    }
};

static LGFX lcd;

bool display_init() {
    fb = (uint16_t *)heap_caps_malloc(LCD_W * LCD_H * sizeof(uint16_t), MALLOC_CAP_DMA | MALLOC_CAP_8BIT);
    if (!fb) return false;
    lcd.init();
    lcd.setBrightness(0);
    return true;
}

void display_push() {
    // rgb565_t = native-endian RGB565, which is what gfx.c writes.
    lcd.startWrite();
    lcd.pushImageDMA(0, 0, LCD_W, LCD_H, (const lgfx::rgb565_t *)fb);
}

void display_wait() {
    lcd.waitDMA();
    lcd.endWrite();
}

void display_backlight(uint8_t level) { lcd.setBrightness(level); }
