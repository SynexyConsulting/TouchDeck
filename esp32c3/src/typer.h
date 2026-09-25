#pragma once

// Types app.clip on the host as Bluetooth keystrokes (US layout, ASCII only).
void typer_start();
void typer_cancel();
bool typer_busy();   // pending or typing
void typer_step();   // call from the logic loop
