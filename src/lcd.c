// Panel driver for both RP boards, one API (lcd.h):
//  - RP2040-Touch-LCD-1.69: ST7789V2 240x280. The visible area starts at row 20
//    of the controller's 320-row RAM, hence Y_OFFSET.
//  - RP2350-Touch-LCD-1.28 (TD_BOARD_RP2350_128): GC9A01A 240x240 round, no offset.
// Only the init sequence and the offset differ; both take RGB565 over SPI.
#include "lcd.h"
#include "board.h"
#include "pico/stdlib.h"
#include "hardware/spi.h"
#include "hardware/dma.h"
#include "hardware/pwm.h"

#ifdef TD_BOARD_RP2350_128
#define Y_OFFSET 0
#else
#define Y_OFFSET 20
#endif

static int dma_chan = -1;

static void cmd(uint8_t c, const uint8_t *data, int n) {
    gpio_put(LCD_PIN_CS, 0);
    gpio_put(LCD_PIN_DC, 0);
    spi_write_blocking(LCD_SPI, &c, 1);
    if (n) {
        gpio_put(LCD_PIN_DC, 1);
        spi_write_blocking(LCD_SPI, data, n);
    }
    gpio_put(LCD_PIN_CS, 1);
}

#define CMD(c, ...) do { static const uint8_t d[] = {__VA_ARGS__}; cmd(c, d, sizeof d); } while (0)

void lcd_set_backlight(uint8_t percent) {
    pwm_set_gpio_level(LCD_PIN_BL, percent > 100 ? 100 : percent);
}

void lcd_init(void) {
    spi_init(LCD_SPI, 62500 * 1000);
    gpio_set_function(LCD_PIN_CLK, GPIO_FUNC_SPI);
    gpio_set_function(LCD_PIN_MOSI, GPIO_FUNC_SPI);

    const uint pins[] = {LCD_PIN_DC, LCD_PIN_CS, LCD_PIN_RST};
    for (int i = 0; i < 3; i++) {
        gpio_init(pins[i]);
        gpio_set_dir(pins[i], GPIO_OUT);
        gpio_put(pins[i], 1);
    }

    gpio_set_function(LCD_PIN_BL, GPIO_FUNC_PWM);
    uint slice = pwm_gpio_to_slice_num(LCD_PIN_BL);
    pwm_set_wrap(slice, 100);
    pwm_set_clkdiv(slice, 50);
    pwm_set_enabled(slice, true);
    lcd_set_backlight(0);

    gpio_put(LCD_PIN_RST, 0); sleep_ms(20);
    gpio_put(LCD_PIN_RST, 1); sleep_ms(120);

#ifdef TD_BOARD_RP2350_128
    // Init sequence from Waveshare's LCD_1in28.c (LCD_1IN28_InitReg, RP2350 demo),
    // converted one command per line. MADCTL 0x08: portrait, BGR.
    cmd(0xEF, 0, 0);
    CMD(0xEB, 0x14);
    cmd(0xFE, 0, 0);
    cmd(0xEF, 0, 0);
    CMD(0xEB, 0x14);
    CMD(0x84, 0x40);
    CMD(0x85, 0xFF);
    CMD(0x86, 0xFF);
    CMD(0x87, 0xFF);
    CMD(0x88, 0x0A);
    CMD(0x89, 0x21);
    CMD(0x8A, 0x00);
    CMD(0x8B, 0x80);
    CMD(0x8C, 0x01);
    CMD(0x8D, 0x01);
    CMD(0x8E, 0xFF);
    CMD(0x8F, 0xFF);
    CMD(0xB6, 0x00, 0x20);
    CMD(0x36, 0x08);
    CMD(0x3A, 0x05);
    CMD(0x90, 0x08, 0x08, 0x08, 0x08);
    CMD(0xBD, 0x06);
    CMD(0xBC, 0x00);
    CMD(0xFF, 0x60, 0x01, 0x04);
    CMD(0xC3, 0x13);
    CMD(0xC4, 0x13);
    CMD(0xC9, 0x22);
    CMD(0xBE, 0x11);
    CMD(0xE1, 0x10, 0x0E);
    CMD(0xDF, 0x21, 0x0C, 0x02);
    CMD(0xF0, 0x45, 0x09, 0x08, 0x08, 0x26, 0x2A);
    CMD(0xF1, 0x43, 0x70, 0x72, 0x36, 0x37, 0x6F);
    CMD(0xF2, 0x45, 0x09, 0x08, 0x08, 0x26, 0x2A);
    CMD(0xF3, 0x43, 0x70, 0x72, 0x36, 0x37, 0x6F);
    CMD(0xED, 0x1B, 0x0B);
    CMD(0xAE, 0x77);
    CMD(0xCD, 0x63);
    CMD(0x70, 0x07, 0x07, 0x04, 0x0E, 0x0F, 0x09, 0x07, 0x08, 0x03);
    CMD(0xE8, 0x34);
    CMD(0x62, 0x18, 0x0D, 0x71, 0xED, 0x70, 0x70, 0x18, 0x0F, 0x71, 0xEF, 0x70, 0x70);
    CMD(0x63, 0x18, 0x11, 0x71, 0xF1, 0x70, 0x70, 0x18, 0x13, 0x71, 0xF3, 0x70, 0x70);
    CMD(0x64, 0x28, 0x29, 0xF1, 0x01, 0xF1, 0x00, 0x07);
    CMD(0x66, 0x3C, 0x00, 0xCD, 0x67, 0x45, 0x45, 0x10, 0x00, 0x00, 0x00);
    CMD(0x67, 0x00, 0x3C, 0x00, 0x00, 0x00, 0x01, 0x54, 0x10, 0x32, 0x98);
    CMD(0x74, 0x10, 0x85, 0x80, 0x00, 0x00, 0x4E, 0x00);
    CMD(0x98, 0x3E, 0x07);
    cmd(0x35, 0, 0);
    cmd(0x21, 0, 0);
    cmd(0x11, 0, 0);
    sleep_ms(120);
    cmd(0x29, 0, 0);
    sleep_ms(20);
#else
    // Init sequence from Waveshare's LCD_1in69.c
    CMD(0x36, 0x00);                         // MADCTL: portrait
    CMD(0x3A, 0x05);                         // 16-bit RGB565
    CMD(0xB2, 0x0B, 0x0B, 0x00, 0x33, 0x35);
    CMD(0xB7, 0x11);
    CMD(0xBB, 0x35);
    CMD(0xC0, 0x2C);
    CMD(0xC2, 0x01);
    CMD(0xC3, 0x0D);
    CMD(0xC4, 0x20);
    CMD(0xC6, 0x13);
    CMD(0xD0, 0xA4, 0xA1);
    CMD(0xD6, 0xA1);
    CMD(0xE0, 0xF0, 0x06, 0x0B, 0x0A, 0x09, 0x26, 0x29, 0x33, 0x41, 0x18, 0x16, 0x15, 0x29, 0x2D);
    CMD(0xE1, 0xF0, 0x04, 0x08, 0x08, 0x07, 0x03, 0x28, 0x32, 0x40, 0x3B, 0x19, 0x18, 0x2A, 0x2E);
    CMD(0xE4, 0x25, 0x00, 0x00);
    cmd(0x21, 0, 0);                         // inversion on
    cmd(0x11, 0, 0);                         // sleep out
    sleep_ms(120);
    cmd(0x29, 0, 0);                         // display on
#endif

    dma_chan = dma_claim_unused_channel(true);
}

void lcd_wait(void) {
    if (dma_chan < 0) return;
    dma_channel_wait_for_finish_blocking(dma_chan);
    while (spi_is_busy(LCD_SPI)) tight_loop_contents();
    gpio_put(LCD_PIN_CS, 1);
    spi_set_format(LCD_SPI, 8, SPI_CPOL_0, SPI_CPHA_0, SPI_MSB_FIRST);
}

void lcd_push_frame(const uint16_t *fb) {
    lcd_wait();
    const uint16_t y0 = Y_OFFSET, y1 = Y_OFFSET + LCD_H - 1, x1 = LCD_W - 1;
    const uint8_t caset[] = {0, 0, x1 >> 8, x1 & 0xFF};
    const uint8_t raset[] = {y0 >> 8, y0 & 0xFF, y1 >> 8, y1 & 0xFF};
    cmd(0x2A, caset, 4);
    cmd(0x2B, raset, 4);
    cmd(0x2C, 0, 0);

    // 16-bit SPI frames send each pixel MSB first, which is the byte order
    // the ST7789 wants, so the framebuffer can stay in native RGB565.
    spi_set_format(LCD_SPI, 16, SPI_CPOL_0, SPI_CPHA_0, SPI_MSB_FIRST);
    gpio_put(LCD_PIN_CS, 0);
    gpio_put(LCD_PIN_DC, 1);

    dma_channel_config c = dma_channel_get_default_config(dma_chan);
    channel_config_set_transfer_data_size(&c, DMA_SIZE_16);
    channel_config_set_dreq(&c, spi_get_dreq(LCD_SPI, true));
    dma_channel_configure(dma_chan, &c, &spi_get_hw(LCD_SPI)->dr, fb, LCD_W * LCD_H, true);
}

// Push just the w x h window at (x, y) of the full-screen framebuffer. Rows of
// the window aren't contiguous in fb, so it is one DMA per row; blocking, but a
// 130-pixel row takes ~40 us at 50 MHz SPI.
void lcd_push_rect(const uint16_t *fb, int x, int y, int w, int h) {
    lcd_wait();
    const uint16_t x1 = x + w - 1, y0 = Y_OFFSET + y, y1 = Y_OFFSET + y + h - 1;
    const uint8_t caset[] = {x >> 8, x & 0xFF, x1 >> 8, x1 & 0xFF};
    const uint8_t raset[] = {y0 >> 8, y0 & 0xFF, y1 >> 8, y1 & 0xFF};
    cmd(0x2A, caset, 4);
    cmd(0x2B, raset, 4);
    cmd(0x2C, 0, 0);

    spi_set_format(LCD_SPI, 16, SPI_CPOL_0, SPI_CPHA_0, SPI_MSB_FIRST);
    gpio_put(LCD_PIN_CS, 0);
    gpio_put(LCD_PIN_DC, 1);
    dma_channel_config c = dma_channel_get_default_config(dma_chan);
    channel_config_set_transfer_data_size(&c, DMA_SIZE_16);
    channel_config_set_dreq(&c, spi_get_dreq(LCD_SPI, true));
    for (int r = 0; r < h; r++) {
        dma_channel_configure(dma_chan, &c, &spi_get_hw(LCD_SPI)->dr, fb + (y + r) * LCD_W + x, w, true);
        dma_channel_wait_for_finish_blocking(dma_chan);
    }
    lcd_wait();
}
