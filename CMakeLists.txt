cmake_minimum_required(VERSION 3.13)

# Toolchain from the VS Code Raspberry Pi Pico extension (~/.pico-sdk) by default. CI (and any
# other machine) sets PICO_SDK_PATH in the environment and uses the arm-none-eabi-gcc on PATH.
# Use Arm GNU Toolchain 14.2.Rel1 (as CI does): distro GCC 13 builds drew the UI 10-30% slower.
if(DEFINED ENV{PICO_SDK_PATH})
    set(PICO_SDK_PATH $ENV{PICO_SDK_PATH})
else()
    set(USERHOME $ENV{USERPROFILE})
    set(PICO_SDK_PATH ${USERHOME}/.pico-sdk/sdk/2.1.1)
    set(PICO_TOOLCHAIN_PATH ${USERHOME}/.pico-sdk/toolchain/14_2_Rel1)
    set(picotool_DIR ${USERHOME}/.pico-sdk/picotool/2.1.1/picotool)
endif()

# Which board to build. The SDK fixes the chip once per configure, so each board
# has its own build directory:
#   rp2040_169 (default)  Waveshare RP2040-Touch-LCD-1.69  -> build/watch.uf2
#   rp2350_128            Waveshare RP2350-Touch-LCD-1.28  -> build-rp2350/deck128.uf2
#     cmake -S . -B build-rp2350 -G Ninja -DCMAKE_BUILD_TYPE=Release -DTD_BOARD=rp2350_128
set(TD_BOARD rp2040_169 CACHE STRING "Board: rp2040_169 or rp2350_128")
if(TD_BOARD STREQUAL "rp2350_128")
    set(PICO_PLATFORM rp2350-arm-s)
    set(PICO_BOARD waveshare_rp2350_touch_lcd_1.28 CACHE STRING "Board type")
    set(TD_TARGET deck128)
elseif(TD_BOARD STREQUAL "rp2040_169")
    set(PICO_BOARD pico CACHE STRING "Board type")
    set(TD_TARGET watch)
else()
    message(FATAL_ERROR "Unknown TD_BOARD '${TD_BOARD}' (rp2040_169 or rp2350_128)")
endif()

include(pico_sdk_import.cmake)

project(${TD_TARGET} C CXX ASM)
set(CMAKE_C_STANDARD 11)
pico_sdk_init()

# Board-independent: USB, typing, jiggler, touch, settings, drawing, mirror sync.
set(TD_COMMON
    src/main.c
    src/gfx.c
    src/button.c
    src/touch.c
    src/ui.c
    src/ui_sync.c
    src/usb_io.c
    src/jig_motion.c
    src/jig_paths.c
    src/jig_lane.c
    src/usb_descriptors.c
    src/typer.c
    src/jiggler.c
    src/settings.c
    src/icons.c
    src/aa_fonts.c
    src/jig_menu.c
)

if(TD_TARGET STREQUAL "deck128")
    # Round GC9A01 panel, the ESP32-C3's round pages (src/round), USB only, no buzzer.
    add_executable(deck128 ${TD_COMMON} src/lcd.c src/round/ui_pages.c)
    target_compile_definitions(deck128 PRIVATE TD_BOARD_RP2350_128 TD_ROUND UI_USB_ONLY)
else()
    # Listed in full, in the original order: the link order sets the flash layout,
    # which moves draw timings by a few ms (flash cache), so keep it stable.
    add_executable(watch
        src/main.c src/lcd.c src/gfx.c src/buzzer.c src/button.c src/touch.c src/ui.c src/ui_pages.c
        src/ui_sync.c src/usb_io.c src/jig_motion.c src/jig_paths.c src/jig_lane.c src/usb_descriptors.c
        src/typer.c src/jiggler.c src/settings.c src/icons.c src/aa_fonts.c src/jig_menu.c)
endif()

# Keep clk_peri (SPI, I2C) on the overclocked system PLL; the SDK default
# moves it to the 48 MHz USB clock, which slowed the LCD push 2.5x.
target_compile_definitions(${TD_TARGET} PRIVATE PICO_CLOCK_ADJUST_PERI_CLOCK_WITH_SYS_CLOCK=1)

# tusb_config.h lives in src/
target_include_directories(${TD_TARGET} PRIVATE src)

target_link_libraries(${TD_TARGET}
    pico_stdlib
    pico_multicore
    pico_rand
    pico_unique_id
    pico_flash
    hardware_flash
    hardware_spi
    hardware_dma
    hardware_pwm
    hardware_i2c
    hardware_vreg
    tinyusb_device
)

# USB is our own composite device (HID + CDC), so no SDK stdio over USB.
pico_enable_stdio_usb(${TD_TARGET} 0)
pico_enable_stdio_uart(${TD_TARGET} 0)
pico_add_extra_outputs(${TD_TARGET})
