#include "process.hpp"

#include <atomic>
#include <chrono>
#include <filesystem>
#include <mutex>
#include <thread>

#include "cmdline.hpp"
#include "util.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>
#endif

namespace fcd::process {

using Clock = std::chrono::steady_clock;

void LineSplitter::feed(const char* data, size_t size) {
    for (size_t i = 0; i < size; ++i) {
        const char c = data[i];
        if (pending_cr_) {
            pending_cr_ = false;
            if (c == '\n') continue;  // "\r\n": the line ended at '\r'
        }
        if (c == '\r' || c == '\n') {
            emit_(line_);
            line_.clear();
            pending_cr_ = c == '\r';
        } else {
            line_ += c;
        }
    }
}

void LineSplitter::finish() {
    if (!line_.empty()) emit_(line_);
    line_.clear();
    pending_cr_ = false;
}

namespace {

// How long output may stay open after the process tree was stopped. Only a process outside the
// tree (one that escaped the job object or process group) can hold the pipes that long.
constexpr int kStoppedOutputGraceMs = 3000;

// Reader threads and line delivery, shared by both platforms.
class ChildBase : public Child {
   public:
    bool output_finished() const override { return open_streams_.load() == 0; }

    bool output_abandoned() const override { return abandoned_.load(); }

    void finish_output(int timeout_ms) override {
        if (!wait_for_output(timeout_ms)) {
            kill_tree();
            if (!wait_for_output(kStoppedOutputGraceMs)) {
                abandoned_.store(true);
                interrupt_readers();
            }
        }
        join_readers();
    }

   protected:
    explicit ChildBase(LineHandler on_line) : on_line_(std::move(on_line)) {}

    // Set by finish_output when the output is given up; the readers then report end of file.
    bool abandoning() const { return abandoned_.load(); }

    // Makes readers blocked in a read notice abandoning(). POSIX readers poll, so they need nothing.
    virtual void interrupt_readers() {}

    // Reads one pipe until end of file. read_some returns the byte count, 0 at end of file.
    template <typename ReadSome>
    void start_reader(int index, Stream stream, ReadSome read_some) {
        readers_[index] = std::thread([this, stream, read_some] {
            LineSplitter splitter([this, stream](const std::string& raw) { deliver(stream, raw); });
            char buffer[4096];
            for (;;) {
                const long n = read_some(buffer, sizeof buffer);
                if (n <= 0) break;
                splitter.feed(buffer, static_cast<size_t>(n));
            }
            splitter.finish();
            open_streams_.fetch_sub(1);
        });
    }

    void join_readers() {
        for (auto& t : readers_)
            if (t.joinable()) t.join();
    }

   private:
    LineHandler on_line_;
    std::mutex deliver_mutex_;
    std::atomic<int> open_streams_{2};
    std::atomic<bool> abandoned_{false};
    std::thread readers_[2];

    bool wait_for_output(int timeout_ms) {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms);
        while (open_streams_.load() > 0 && Clock::now() < deadline) std::this_thread::sleep_for(std::chrono::milliseconds(10));
        return open_streams_.load() == 0;
    }

    void deliver(Stream stream, const std::string& raw) {
        if (!on_line_) return;
        std::lock_guard lock(deliver_mutex_);
        try {
            on_line_(stream, to_utf8_lenient(raw));
        } catch (...) {
            // A failing handler must not end the reader thread (that would terminate the program).
        }
    }
};

std::filesystem::path check_program(const std::string& program) {
    if (trim(program).empty()) throw Error(FCD_ERR_ARGUMENT, "no program to run");
    const auto path = utf8_path(program);
    std::error_code ec;
    if (!std::filesystem::is_regular_file(path, ec)) throw Error(FCD_ERR_NOT_FOUND, "Program not found: " + program);
    return std::filesystem::absolute(path, ec);
}

std::filesystem::path working_directory(const Options& o, const std::filesystem::path& program) {
    return o.working_directory.empty() ? program.parent_path() : utf8_path(o.working_directory);
}

#ifdef _WIN32

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::string windows_error(const char* what) {
    return std::string(what) + " failed (Windows error " + std::to_string(GetLastError()) + ")";
}

class WinChild final : public ChildBase {
   public:
    WinChild(const Options& options, LineHandler on_line) : ChildBase(std::move(on_line)) {
        const auto program = check_program(options.program);
        const auto cwd = working_directory(options, program);

        SECURITY_ATTRIBUTES sa{sizeof sa, nullptr, TRUE};
        HANDLE out_w = nullptr, err_w = nullptr, in_r = nullptr;
        if (!CreatePipe(&out_r_, &out_w, &sa, 0) || !CreatePipe(&err_r_, &err_w, &sa, 0))
            throw Error(FCD_ERR_IO, windows_error("CreatePipe"));
        SetHandleInformation(out_r_, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(err_r_, HANDLE_FLAG_INHERIT, 0);
        if (options.pipe_stdin) {
            if (!CreatePipe(&in_r, &stdin_w_, &sa, 0)) throw Error(FCD_ERR_IO, windows_error("CreatePipe"));
            SetHandleInformation(stdin_w_, HANDLE_FLAG_INHERIT, 0);
        } else {
            in_r = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, nullptr);
            if (in_r == INVALID_HANDLE_VALUE) throw Error(FCD_ERR_IO, windows_error("Opening NUL"));
        }

        // Only these three handles are inherited, so a process started concurrently from another
        // thread cannot pick up (and keep open) the write ends of these pipes.
        HANDLE inherited[3] = {in_r, out_w, err_w};
        SIZE_T size = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        std::vector<unsigned char> attributes(size);
        auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        if (!InitializeProcThreadAttributeList(list, 1, 0, &size) ||
            !UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof inherited, nullptr,
                                       nullptr))
            throw Error(FCD_ERR_IO, windows_error("Preparing the inherited handles"));

        STARTUPINFOEXW si{};
        si.StartupInfo.cb = sizeof si;
        si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        si.StartupInfo.hStdInput = in_r;
        si.StartupInfo.hStdOutput = out_w;
        si.StartupInfo.hStdError = err_w;
        si.lpAttributeList = list;

        const auto program_u8 = program.u8string();
        std::vector<std::string> argv{std::string(program_u8.begin(), program_u8.end())};
        argv.insert(argv.end(), options.args.begin(), options.args.end());
        std::wstring command_line = widen(cmdline::format(argv));
        const std::wstring directory = cwd.wstring();

        PROCESS_INFORMATION pi{};
        const BOOL ok = CreateProcessW(nullptr, command_line.data(), nullptr, nullptr, TRUE,
                                       CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT |
                                           CREATE_UNICODE_ENVIRONMENT,
                                       nullptr, directory.empty() ? nullptr : directory.c_str(), &si.StartupInfo, &pi);
        const DWORD create_error = GetLastError();
        DeleteProcThreadAttributeList(list);
        CloseHandle(in_r);
        CloseHandle(out_w);
        CloseHandle(err_w);
        if (!ok) {
            CloseHandle(out_r_);
            CloseHandle(err_r_);
            if (stdin_w_) CloseHandle(stdin_w_);
            throw Error(FCD_ERR_IO, "Could not start " + options.program + " (Windows error " +
                                        std::to_string(create_error) + ")");
        }
        process_ = pi.hProcess;

        // A job object ties everything the process starts (fastchess starts the engines) to this handle.
        job_ = CreateJobObjectW(nullptr, nullptr);
        if (job_) {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(job_, JobObjectExtendedLimitInformation, &limits, sizeof limits);
            if (!AssignProcessToJobObject(job_, process_)) {
                CloseHandle(job_);
                job_ = nullptr;  // fall back to stopping the process alone
            }
        }
        ResumeThread(pi.hThread);
        CloseHandle(pi.hThread);

        const HANDLE out = out_r_, err = err_r_;
        auto reader = [this](HANDLE h, int index) {
            return [this, h, index](char* buffer, size_t size) -> long {
                register_reader_thread(index);
                if (abandoning()) return 0;
                DWORD n = 0;
                // Fails with ERROR_OPERATION_ABORTED when interrupt_readers cancels it.
                if (!ReadFile(h, buffer, static_cast<DWORD>(size), &n, nullptr)) return 0;
                return static_cast<long>(n);
            };
        };
        start_reader(0, Stream::Stdout, reader(out, 0));
        start_reader(1, Stream::Stderr, reader(err, 1));
    }

    ~WinChild() override {
        if (!wait(0)) {
            kill_tree();
            wait(-1);
        }
        finish_output(2000);
        if (stdin_w_) CloseHandle(stdin_w_);
        CloseHandle(out_r_);
        CloseHandle(err_r_);
        CloseHandle(process_);
        if (job_) CloseHandle(job_);  // stops anything still running in the job
        for (auto& t : reader_threads_)
            if (const HANDLE h = t.load()) CloseHandle(h);
    }

    void write_line(const std::string& line) override {
        std::lock_guard lock(write_mutex_);
        if (!stdin_w_) throw Error(FCD_ERR_PROCESS, "the process has no input pipe");
        const std::string data = line + "\n";
        DWORD written = 0;
        if (!WriteFile(stdin_w_, data.data(), static_cast<DWORD>(data.size()), &written, nullptr) ||
            written != data.size())
            throw Error(FCD_ERR_PROCESS, "the process no longer accepts input");
    }

    bool wait(int timeout_ms) override {
        if (exited_) return true;
        if (WaitForSingleObject(process_, timeout_ms < 0 ? INFINITE : static_cast<DWORD>(timeout_ms)) != WAIT_OBJECT_0)
            return false;
        DWORD code = 0;
        GetExitCodeProcess(process_, &code);
        exit_code_ = static_cast<int>(code);
        exited_ = true;
        return true;
    }

    int exit_code() const override { return exit_code_; }

    void kill_tree() override {
        if (job_) TerminateJobObject(job_, 1);
        else TerminateProcess(process_, 1);
    }

   protected:
    // A reader blocked in ReadFile on a pipe that another process keeps open never returns on its
    // own. Cancel its read until it has stopped; the loop also covers a reader that was between
    // its abandoning() check and ReadFile when the first cancel came.
    void interrupt_readers() override {
        const auto deadline = Clock::now() + std::chrono::milliseconds(kStoppedOutputGraceMs);
        while (!output_finished() && Clock::now() < deadline) {
            for (auto& t : reader_threads_)
                if (const HANDLE h = t.load()) CancelSynchronousIo(h);
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    }

   private:
    HANDLE process_ = nullptr, job_ = nullptr, stdin_w_ = nullptr, out_r_ = nullptr, err_r_ = nullptr;
    std::atomic<HANDLE> reader_threads_[2]{};
    std::mutex write_mutex_;
    int exit_code_ = 0;
    bool exited_ = false;

    // Called on the reader thread: keeps a real handle to it for CancelSynchronousIo.
    void register_reader_thread(int index) {
        if (reader_threads_[index].load()) return;
        HANDLE self = nullptr;
        if (DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &self, 0, FALSE,
                            DUPLICATE_SAME_ACCESS))
            reader_threads_[index].store(self);
    }
};

#else  // POSIX

void ignore_sigpipe() {
    // Writing to an engine that has exited must fail with EPIPE instead of killing the program.
    // .NET already ignores SIGPIPE; this covers other hosts such as the native tests.
    static std::once_flag once;
    std::call_once(once, [] {
        struct sigaction current {};
        if (sigaction(SIGPIPE, nullptr, &current) == 0 && current.sa_handler == SIG_DFL) signal(SIGPIPE, SIG_IGN);
    });
}

void make_pipe(int fds[2]) {
    if (pipe(fds) != 0) throw Error(FCD_ERR_IO, std::string("pipe failed: ") + std::strerror(errno));
    fcntl(fds[0], F_SETFD, FD_CLOEXEC);
    fcntl(fds[1], F_SETFD, FD_CLOEXEC);
}

class PosixChild final : public ChildBase {
   public:
    PosixChild(const Options& options, LineHandler on_line) : ChildBase(std::move(on_line)) {
        ignore_sigpipe();
        const auto program = check_program(options.program);
        if (access(program.c_str(), X_OK) != 0) throw Error(FCD_ERR_IO, "Program is not executable: " + options.program);
        const std::string cwd = working_directory(options, program).string();

        int out[2], err[2], in[2] = {-1, -1};
        make_pipe(out);
        make_pipe(err);
        if (options.pipe_stdin) make_pipe(in);
        const int null_fd = options.pipe_stdin ? -1 : open("/dev/null", O_RDONLY | O_CLOEXEC);

        // Everything the child needs is prepared before fork: after it, only async-signal-safe calls.
        std::vector<std::string> args{program.string()};
        args.insert(args.end(), options.args.begin(), options.args.end());
        std::vector<char*> argv;
        for (auto& a : args) argv.push_back(a.data());
        argv.push_back(nullptr);
        const std::string exe = program.string();

        pid_ = fork();
        if (pid_ < 0) throw Error(FCD_ERR_IO, std::string("fork failed: ") + std::strerror(errno));
        if (pid_ == 0) {
            setpgid(0, 0);  // a process group of its own, so the whole tree can be stopped
            dup2(options.pipe_stdin ? in[0] : null_fd, 0);
            dup2(out[1], 1);
            dup2(err[1], 2);
            if (!cwd.empty() && chdir(cwd.c_str()) != 0) _exit(127);
            execv(exe.c_str(), argv.data());
            _exit(127);
        }
        setpgid(pid_, pid_);  // also here, whichever runs first
        close(out[1]);
        close(err[1]);
        if (options.pipe_stdin) {
            close(in[0]);
            stdin_w_ = in[1];
        }
        if (null_fd >= 0) close(null_fd);
        out_r_ = out[0];
        err_r_ = err[0];

        // Waits in poll rather than read, so a reader notices abandoning() within 100 ms.
        auto reader = [this](int fd) {
            return [this, fd](char* buffer, size_t size) -> long {
                for (;;) {
                    if (abandoning()) return 0;
                    pollfd p{fd, POLLIN, 0};
                    const int ready = poll(&p, 1, 100);
                    if (ready == 0 || (ready < 0 && errno == EINTR)) continue;
                    if (ready < 0) return 0;
                    const ssize_t n = read(fd, buffer, size);
                    if (n < 0 && (errno == EINTR || errno == EAGAIN)) continue;
                    return static_cast<long>(n < 0 ? 0 : n);
                }
            };
        };
        start_reader(0, Stream::Stdout, reader(out_r_));
        start_reader(1, Stream::Stderr, reader(err_r_));
    }

    ~PosixChild() override {
        if (!wait(0)) {
            kill_tree();
            wait(-1);
        }
        finish_output(2000);
        if (stdin_w_ >= 0) close(stdin_w_);
        close(out_r_);
        close(err_r_);
    }

    void write_line(const std::string& line) override {
        std::lock_guard lock(write_mutex_);
        if (stdin_w_ < 0) throw Error(FCD_ERR_PROCESS, "the process has no input pipe");
        const std::string data = line + "\n";
        size_t done = 0;
        while (done < data.size()) {
            const ssize_t n = ::write(stdin_w_, data.data() + done, data.size() - done);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) throw Error(FCD_ERR_PROCESS, "the process no longer accepts input");
            done += static_cast<size_t>(n);
        }
    }

    bool wait(int timeout_ms) override {
        if (exited_) return true;
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeout_ms < 0 ? 0 : timeout_ms);
        for (;;) {
            int status = 0;
            const pid_t r = waitpid(pid_, &status, WNOHANG);
            if (r == pid_) {
                exit_code_ = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : -1;
                exited_ = true;
                return true;
            }
            if (r < 0 && errno != EINTR) {  // already reaped or not our child
                exited_ = true;
                exit_code_ = -1;
                return true;
            }
            if (timeout_ms >= 0 && Clock::now() >= deadline) return false;
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
    }

    int exit_code() const override { return exit_code_; }

    // The process group outlives its leader while any member runs (for example engines that keep
    // the output pipes open after fastchess exited), so the group is signalled in every case.
    void kill_tree() override {
        ::kill(-pid_, SIGKILL);
        if (!exited_) ::kill(pid_, SIGKILL);
    }

   private:
    pid_t pid_ = -1;
    int stdin_w_ = -1, out_r_ = -1, err_r_ = -1;
    std::mutex write_mutex_;
    int exit_code_ = 0;
    bool exited_ = false;
};

#endif

}  // namespace

std::unique_ptr<Child> Child::start(const Options& options, LineHandler on_line) {
#ifdef _WIN32
    return std::make_unique<WinChild>(options, std::move(on_line));
#else
    return std::make_unique<PosixChild>(options, std::move(on_line));
#endif
}

RunResult run(const Options& options, const LineHandler& on_line, const Cancel* cancel) {
    const auto started = Clock::now();
    auto child = Child::start(options, on_line);
    RunResult result;
    while (!child->wait(50)) {
        if (cancelled(cancel)) {
            result.cancelled = true;
            child->kill_tree();
            child->wait(-1);
            break;
        }
    }
    // Normally the output ends with the process. Engines left running by a crashed fastchess keep
    // the pipes open; after a grace period they are stopped with the rest of the tree. After a
    // cancel the tree was stopped already, so only a short wait for the last output is needed.
    child->finish_output(result.cancelled ? 1000 : 5000);
    if (child->output_abandoned() && on_line) {
        const auto name = utf8_path(options.program).filename().u8string();
        on_line(Stream::Stderr, "fcd: the output of " + std::string(name.begin(), name.end()) +
                                    " was still open after its processes were stopped; a process outside the "
                                    "process tree holds it. Stopped reading it.");
    }
    result.exit_code = child->exit_code();
    result.duration_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started).count();
    return result;
}

}  // namespace fcd::process
