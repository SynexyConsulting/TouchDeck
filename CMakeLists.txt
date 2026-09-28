cmake_minimum_required(VERSION 3.13)

# Toolchain from the VS Code Raspberry Pi Pico extension (~/.pico-sdk).
set(USERHOME $ENV{USERPROFILE})
set(PICO_SDK_PATH ${USERHOME}/.pico-sdk/sdk/2.1.1)
set(PICO_TOOLCHAIN_PATH ${USERHOME}/.pico-sdk/toolchain/14_2_Rel1)
set(picotool_DIR ${USERHOME}/.pico-sdk/picotool/2.1.1/picotool)
set(PICO_BOARD pico CACHE STRING "Board type")

include(pico_sdk_import.cmake)

project(watch C CXX ASM)
set(CMAKE_C_STANDARD 11)
pico_sdk_init()

add_executable(watch
    src/main.c
    src/lcd.c
    src/gfx.c
    src/buzzer.c
    src/button.c
    src/touch.c
    src/ui.c
    src/usb_io.c
    src/usb_descriptors.c
    src/typer.c
    src/jiggler.c
    src/settings.c
    src/icons.c
    src/aa_fonts.c
)

# Keep clk_peri (SPI, I2C) on the overclocked system PLL; the SDK default
# moves it to the 48 MHz USB clock, which slowed the LCD push 2.5x.
target_compile_definitions(watch PRIVATE PICO_CLOCK_ADJUST_PERI_CLOCK_WITH_SYS_CLOCK=1)

# tusb_config.h lives in src/
target_include_directories(watch PRIVATE src)

target_link_libraries(watch
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
pico_enable_stdio_usb(watch 0)
pico_enable_stdio_uart(watch 0)
pico_add_extra_outputs(watch)
