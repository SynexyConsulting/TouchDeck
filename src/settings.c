// Settings live in the last flash sector, far past the ~230 KB program image.
// Each save programs the next unused 256-byte page, and the newest valid page
// wins on load. The sector is erased only once all 16 pages are used, which
// cuts erase wear 16x and makes most saves a ~1 ms page program instead of a
// ~50 ms sector erase.
#include <string.h>
#include "pico/stdlib.h"
#include "pico/flash.h"
#include "hardware/flash.h"
#include "app.h"
#include "settings.h"

#define SETTINGS_OFFSET (PICO_FLASH_SIZE_BYTES - FLASH_SECTOR_SIZE)
#define PAGES (FLASH_SECTOR_SIZE / FLASH_PAGE_SIZE)
#define MAGIC    0x324B4454u   // "TDK2"
#define MAGIC_V1 0x314B4454u   // "TDK1": {magic, muted, check}, still readable

typedef struct {
    uint32_t magic;
    uint32_t muted;
    uint32_t jig_on;
    uint32_t check;          // guards against a page half-written at power loss
} record_t;

static int next_page;        // first erased page, PAGES if the sector is full
static record_t current;

static const record_t *page_at(int i) {
    return (const record_t *)(XIP_BASE + SETTINGS_OFFSET + i * FLASH_PAGE_SIZE);
}

static uint32_t check_of(const record_t *r) {
    return r->magic ^ r->muted ^ (r->jig_on << 1) ^ 0xA5A5A5A5u;
}

void settings_load(void) {
    current = (record_t){MAGIC, 0, 0, 0};
    next_page = PAGES;
    for (int i = 0; i < PAGES; i++) {
        const record_t *r = page_at(i);
        if (r->magic == 0xFFFFFFFFu) {       // erased: this is where the next save goes
            next_page = i;
            break;
        }
        if (r->magic == MAGIC && r->check == check_of(r)) {
            current = *r;
        } else if (r->magic == MAGIC_V1) {
            const uint32_t *w = (const uint32_t *)r;   // v1 layout: check was word 2
            if (w[2] == (MAGIC_V1 ^ w[1] ^ 0xA5A5A5A5u)) {
                current.muted = w[1];
                current.jig_on = 0;
            }
        }
    }
    app.muted = current.muted != 0;
    app.jig_on = false;   // the caller starts the jiggler via jiggler_set()
}

bool settings_jig_on(void) { return current.jig_on != 0; }

typedef struct {
    bool erase;
    int page;
    const uint8_t *data;
} write_op_t;

// Runs with interrupts off and core1 parked, so nothing executes from flash.
static void do_write(void *p) {
    const write_op_t *op = p;
    if (op->erase) flash_range_erase(SETTINGS_OFFSET, FLASH_SECTOR_SIZE);
    flash_range_program(SETTINGS_OFFSET + op->page * FLASH_PAGE_SIZE, op->data, FLASH_PAGE_SIZE);
}

void settings_save(void) {
    record_t r = {MAGIC, app.muted ? 1u : 0u, app.jig_on ? 1u : 0u, 0};
    r.check = check_of(&r);
    if (current.magic == MAGIC && r.muted == current.muted && r.jig_on == current.jig_on) return;

    static uint8_t page[FLASH_PAGE_SIZE];
    memset(page, 0xFF, sizeof page);
    memcpy(page, &r, sizeof r);

    write_op_t op = {next_page >= PAGES, next_page >= PAGES ? 0 : next_page, page};
    if (flash_safe_execute(do_write, &op, 100) != PICO_OK) return;
    current = r;
    next_page = op.page + 1;
}
