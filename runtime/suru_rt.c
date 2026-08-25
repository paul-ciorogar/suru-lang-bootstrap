/*
 * suru_rt.c — the test channel, program side.
 *
 * Linked into `suru test` builds only; a production build has never heard of it. The emitted
 * program calls three functions and knows nothing else about the channel: where it goes, how a
 * frame is spelled and what happens when there is no harness all live here.
 *
 * The wire format is doc/test-protocol.md. This file is written from that document rather than
 * from the C# reader, which is the point of having written the document.
 *
 * POSIX only, deliberately: AF_UNIX on both sides, and `suru test` already needed a `cc`.
 */

#include <errno.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

/* The environment variable the driver hands the child, holding the socket's path. */
#define SURU_CHANNEL_ENV "SURU_TEST_CHANNEL"

/*
 * A frame accumulates whole because Content-Length precedes the body: nothing can be written
 * until the last field has been added and the total is known.
 */
#define SURU_BODY_MAX 65536

/* One field's rendered value, before it is measured and copied into the body. */
#define SURU_SCRATCH_MAX 8192

/* Set once at startup; -1 means "no harness", and every exported function is then a no-op. */
static int suru_channel = -1;

static char suru_body[SURU_BODY_MAX];
static size_t suru_body_len = 0;

/*
 * Sticky for the frame being built. Once a field has not fit, the body no longer says what the
 * program meant, so it is dropped rather than sent short — see suru_frame_end.
 */
static int suru_overflowed = 0;
static const char *suru_overflow_key = "";

/*
 * Reads the channel's path and connects. A missing variable is not an error: a test binary run
 * by hand still runs, it just drops its records.
 */
__attribute__((constructor)) static void suru_rt_open(void)
{
    const char *path;
    struct sockaddr_un addr;
    int fd;

    path = getenv(SURU_CHANNEL_ENV);
    if (path == NULL || *path == '\0')
        return;

    /*
     * A harness that has gone away must not kill this process with a signal, which the driver
     * would have to read as a crash. MSG_NOSIGNAL is not portable to macOS and SO_NOSIGPIPE is
     * not portable off it; ignoring the signal process-wide costs nothing in a translation unit
     * that exists only in test builds.
     */
    signal(SIGPIPE, SIG_IGN);

    memset(&addr, 0, sizeof addr);
    addr.sun_family = AF_UNIX;
    if (strlen(path) >= sizeof addr.sun_path)
        return; /* longer than sun_path holds: no connection, records dropped */
    memcpy(addr.sun_path, path, strlen(path));

    fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return;

    if (connect(fd, (struct sockaddr *)&addr, sizeof addr) == 0)
        suru_channel = fd;
    else
        close(fd);
}

__attribute__((destructor)) static void suru_rt_close(void)
{
    if (suru_channel >= 0) {
        close(suru_channel);
        suru_channel = -1;
    }
}

/*
 * A stream socket may take fewer bytes than it was offered, and may be interrupted before it
 * takes any. Anything else is the harness gone: the channel closes and the program falls silent,
 * which the driver sees as a run that never finished.
 */
static void suru_write_all(const char *bytes, size_t length)
{
    size_t written = 0;

    while (written < length) {
        ssize_t n = write(suru_channel, bytes + written, length - written);
        if (n > 0) {
            written += (size_t)n;
            continue;
        }
        if (n < 0 && errno == EINTR)
            continue;

        close(suru_channel);
        suru_channel = -1;
        return;
    }
}

/* Appends to the body, or trips the overflow flag on behalf of `key`. */
static void suru_append(const char *key, const char *bytes, size_t length)
{
    if (suru_overflowed)
        return;

    if (length > SURU_BODY_MAX - suru_body_len) {
        suru_overflowed = 1;
        suru_overflow_key = key;
        return;
    }

    memcpy(suru_body + suru_body_len, bytes, length);
    suru_body_len += length;
}

/* Writes `body` as one frame: the header that measures it, then the body itself. */
static void suru_send(const char *body, size_t length)
{
    char header[64];
    int header_len;

    if (suru_channel < 0)
        return;

    header_len = snprintf(header, sizeof header, "Content-Length: %zu\r\n\r\n", length);
    if (header_len < 0 || (size_t)header_len >= sizeof header)
        return; /* unreachable for any length the body can hold */

    /*
     * Two writes rather than one buffer: the reader is a streaming cursor, so a frame split
     * across writes is a non-event, and the alternative is reserving header space in a buffer
     * whose whole job is to hold the body.
     */
    suru_write_all(header, (size_t)header_len);
    if (suru_channel >= 0)
        suru_write_all(body, length);
}

void suru_frame_begin(void)
{
    suru_body_len = 0;
    suru_overflowed = 0;
    suru_overflow_key = "";
}

/*
 * One field: `key ":" byte-length ":" bytes "\n"`. The length counts bytes, which is what lets a
 * value hold a newline or a colon without any escaping — the reader measures rather than scans.
 *
 * The varargs shape is printf's, so codegen passes the same format strings printLn already uses
 * and the two cannot disagree about how a value looks.
 */
void suru_field(const char *key, const char *format, ...)
{
    char scratch[SURU_SCRATCH_MAX];
    char prefix[64];
    va_list args;
    int rendered;
    int prefix_len;

    if (suru_channel < 0 || suru_overflowed)
        return;

    va_start(args, format);
    rendered = vsnprintf(scratch, sizeof scratch, format, args);
    va_end(args);

    if (rendered < 0 || (size_t)rendered >= sizeof scratch) {
        /* vsnprintf truncated: the value on the wire would not be the value computed. */
        suru_overflowed = 1;
        suru_overflow_key = key;
        return;
    }

    prefix_len = snprintf(prefix, sizeof prefix, "%s:%d:", key, rendered);
    if (prefix_len < 0 || (size_t)prefix_len >= sizeof prefix) {
        suru_overflowed = 1;
        suru_overflow_key = key;
        return;
    }

    suru_append(key, prefix, (size_t)prefix_len);
    suru_append(key, scratch, (size_t)rendered);
    suru_append(key, "\n", 1);
}

/*
 * Sends the frame. An overflowed frame is replaced, never truncated: a frame short of its own
 * declared length is the one thing framing exists to prevent, and a reader that met one could
 * only stop. Saying "there was more here than would fit, in this field" costs one frame and
 * leaves the channel usable.
 */
void suru_frame_end(void)
{
    char replacement[128];
    int length;

    if (suru_channel < 0)
        return;

    if (!suru_overflowed) {
        suru_send(suru_body, suru_body_len);
        return;
    }

    length = snprintf(replacement, sizeof replacement, "kind:8:overflow\nkey:%zu:%s\n",
                      strlen(suru_overflow_key), suru_overflow_key);

    /* A key too long to name is still an overflow worth reporting, just an anonymous one. */
    if (length < 0 || (size_t)length >= sizeof replacement)
        length = snprintf(replacement, sizeof replacement, "kind:8:overflow\nkey:0:\n");

    if (length > 0 && (size_t)length < sizeof replacement)
        suru_send(replacement, (size_t)length);

    suru_body_len = 0;
    suru_overflowed = 0;
}
