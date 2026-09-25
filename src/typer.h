#pragma once
#include <stdbool.h>

// Types app.clip on the host as keystrokes (US layout, ASCII only).
void typer_start(void);
void typer_cancel(void);
bool typer_busy(void);   // pending or typing
void typer_step(void);   // call from the main loop
