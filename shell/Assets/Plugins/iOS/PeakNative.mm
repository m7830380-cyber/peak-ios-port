// PEAK iOS native helpers, called from C# through DllImport("__Internal").
//
//  * PeakCrash_*  a fatal signal appends "FATAL SIGNAL" + a native backtrace to Documents/PEAK-iOS.log.
#import <Foundation/Foundation.h>
#include <signal.h>
#include <execinfo.h>
#include <fcntl.h>
#include <unistd.h>
#include <string.h>
#include <stdint.h>

// ======================================================================================================
// Crash logging: the previous handler (Unity's crash reporter / the default) still runs afterwards.
// ======================================================================================================
static char gCrashLogPath[1024];
static struct sigaction gPrevHandlers[NSIG];
static volatile sig_atomic_t gInCrash;

static void PeakWrite(int fd, const char *s) { if (s) write(fd, s, strlen(s)); }

static void PeakCrashHandler(int sig, siginfo_t *info, void *ctx)
{
    if (!gInCrash)
    {
        gInCrash = 1;
        int fd = open(gCrashLogPath, O_WRONLY | O_APPEND | O_CREAT, 0644);
        if (fd >= 0)
        {
            char buf[128];
            snprintf(buf, sizeof(buf), "\n=== FATAL SIGNAL %d (%s) at address %p ===\nnative backtrace:\n", sig, sys_signame[sig], info ? info->si_addr : NULL);
            PeakWrite(fd, buf);
            void *frames[64];
            int n = backtrace(frames, 64);
            backtrace_symbols_fd(frames, n, fd);
            PeakWrite(fd, "=== end of crash report ===\n");
            close(fd);
        }
    }
    struct sigaction *prev = &gPrevHandlers[sig];
    if (prev->sa_flags & SA_SIGINFO) { if (prev->sa_sigaction) { prev->sa_sigaction(sig, info, ctx); return; } }
    else if (prev->sa_handler != SIG_DFL && prev->sa_handler != SIG_IGN && prev->sa_handler != NULL) { prev->sa_handler(sig); return; }
    signal(sig, SIG_DFL);
    raise(sig);
}

extern "C" void PeakCrash_Install(const char *logPath)
{
    if (logPath == NULL) return;
    strlcpy(gCrashLogPath, logPath, sizeof(gCrashLogPath));
    int sigs[] = { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT, SIGTRAP };
    for (int s : sigs)
    {
        struct sigaction sa;
        memset(&sa, 0, sizeof(sa));
        sa.sa_sigaction = PeakCrashHandler;
        sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&sa.sa_mask);
        sigaction(s, &sa, &gPrevHandlers[s]);
    }
    NSLog(@"[PEAK-iOS] crash logger installed -> %s", logPath);
}
