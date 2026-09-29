#include <math.h>
#include "jig_motion.h"

#define SPEED   920.f    // box units/s (~90 px/s on the 1.69's 98 px box, ~3.4 s per O)
#define GLIDE_S 0.5f
#define NORMAL_TAU 0.06f // s: how fast the wander direction turns through a corner

static int nseg(const jig_path_t *p) { return p->n - 1 + (p->closed ? 1 : 0); }

// Point at arc position s, and the unit normal of the segment it is on.
static void point_at(const jig_path_t *p, float s, float *x, float *y, float *nx, float *ny) {
    int ns = nseg(p), i = 0;
    while (i < ns - 1 && p->cum[i + 1] < s) i++;
    const int16_t *a = p->pts[i], *b = p->pts[(i + 1) % p->n];
    float seg = p->cum[i + 1] - p->cum[i];
    float u = seg > 0.f ? (s - p->cum[i]) / seg : 0.f;
    if (u < 0.f) u = 0.f; else if (u > 1.f) u = 1.f;
    float dx = (float)(b[0] - a[0]), dy = (float)(b[1] - a[1]), l = sqrtf(dx * dx + dy * dy);
    *x = a[0] + dx * u;
    *y = a[1] + dy * u;
    *nx = l > 0.f ? -dy / l : 0.f;
    *ny = l > 0.f ? dx / l : 0.f;
}

static void start_point(int letter, float *x, float *y) {
    *x = JIG_PATHS[letter].pts[0][0];
    *y = JIG_PATHS[letter].pts[0][1];
}

void jm_begin(jig_motion_t *m, int letter) {
    m->letter = letter;
    m->s = 0.f;
    m->dir = 1.f;
    m->t = 0.f;
    m->glide = -1.f;
    start_point(letter, &m->x, &m->y);
    m->ax = m->x;
    m->ay = m->y;
    float px, py;
    point_at(&JIG_PATHS[letter], 0.f, &px, &py, &m->nx, &m->ny);
}

void jm_switch(jig_motion_t *m, int letter) {
    m->letter = letter;
    m->glide = 0.f;
    m->gx = m->x;
    m->gy = m->y;
}

int jm_gliding(const jig_motion_t *m) { return m->glide >= 0.f; }

// Sideways offset inside the lane: two slow sines, |w| <= JIG_WANDER, 0 at t = 0.
static float wander(float t) {
    return JIG_WANDER * (0.6f * sinf(t * 0.9f) + 0.4f * sinf(t * 2.3f));
}

void jm_step(jig_motion_t *m, float dt) {
    const jig_path_t *p = &JIG_PATHS[m->letter];
    m->t += dt;
    if (m->glide >= 0.f) {                          // straight, eased glide to the new start
        m->glide += dt / GLIDE_S;
        float u = m->glide >= 1.f ? 1.f : m->glide;
        u = u * u * (3.f - 2.f * u);
        float sx, sy;
        start_point(m->letter, &sx, &sy);
        m->x = m->gx + (sx - m->gx) * u;
        m->y = m->gy + (sy - m->gy) * u;
        if (m->glide >= 1.f) {
            m->glide = -1.f;
            m->s = 0.f;
            m->dir = 1.f;
            float px, py;
            point_at(p, 0.f, &px, &py, &m->nx, &m->ny);
            m->t = 0.f;                             // wander restarts at 0: no jump off the start point
        }
        return;
    }
    float v = SPEED * (1.f + 0.3f * sinf(m->t * 0.37f));
    m->s += m->dir * v * dt;
    if (p->closed) {
        while (m->s >= p->len) m->s -= p->len;
        while (m->s < 0.f) m->s += p->len;
    } else if (m->s > p->len) {
        m->s = 2.f * p->len - m->s;
        m->dir = -1.f;
    } else if (m->s < 0.f) {
        m->s = -m->s;
        m->dir = 1.f;
    }
    float px, py, nx, ny;
    point_at(p, m->s, &px, &py, &nx, &ny);
    // Turn the wander direction smoothly through corners, so the dot never jumps.
    float a = dt / (dt + NORMAL_TAU);
    m->nx += (nx - m->nx) * a;
    m->ny += (ny - m->ny) * a;
    float nl = sqrtf(m->nx * m->nx + m->ny * m->ny);
    float w = wander(m->t) / (nl > 1.f ? nl : 1.f);   // never longer than JIG_WANDER
    m->x = px + m->nx * w;
    m->y = py + m->ny * w;
}

int jm_pick(int current, uint32_t rnd) {
    if (current < 0) return (int)(rnd % JIG_PATH_COUNT);
    int i = (int)(rnd % (JIG_PATH_COUNT - 1));
    return i >= current ? i + 1 : i;
}

void jm_mouse(const jig_motion_t *m, float scale, float *mx, float *my) {
    *mx = JIG_PX_PER_UNIT * scale * (m->x - m->ax);
    *my = JIG_PX_PER_UNIT * scale * (m->y - m->ay);
}
