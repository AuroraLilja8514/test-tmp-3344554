#include <windows.h>
#include <detours.h>

#include <algorithm>
#include <cwchar>
#include <cwctype>
#include <string>
#include <vector>

namespace {

std::string g_timezone_shim_path_ansi;

static decltype(&CreateProcessW) RealCreateProcessW = CreateProcessW;
static decltype(&CreateProcessA) RealCreateProcessA = CreateProcessA;

std::wstring GetEnvironmentString(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return {};
    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) return {};
    value.resize(written);
    return value;
}

std::wstring CurrentExecutablePath() {
    std::vector<wchar_t> buffer(32768, L'\0');
    const DWORD written =
        GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (written == 0 || written >= buffer.size()) return {};
    return std::wstring(buffer.data(), written);
}

std::wstring ModulePath(HINSTANCE instance) {
    std::vector<wchar_t> buffer(32768, L'\0');
    const DWORD written =
        GetModuleFileNameW(instance, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (written == 0 || written >= buffer.size()) return {};
    return std::wstring(buffer.data(), written);
}

std::wstring ToLower(std::wstring value) {
    std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
        return static_cast<wchar_t>(towlower(ch));
    });
    return value;
}

std::wstring BaseNameLower(const std::wstring& path) {
    if (path.empty()) return {};
    const size_t separator = path.find_last_of(L"\\/");
    return ToLower(separator == std::wstring::npos ? path : path.substr(separator + 1));
}

std::wstring FirstCommandToken(const std::wstring& command_line) {
    const wchar_t* cursor = command_line.c_str();
    while (*cursor != L'\0' && iswspace(*cursor)) ++cursor;
    if (*cursor == L'\0') return {};

    std::wstring token;
    if (*cursor == L'\"') {
        ++cursor;
        while (*cursor != L'\0' && *cursor != L'\"') token.push_back(*cursor++);
    } else {
        while (*cursor != L'\0' && !iswspace(*cursor)) token.push_back(*cursor++);
    }
    return token;
}

std::wstring AnsiToWide(const char* value) {
    if (value == nullptr || *value == '\0') return {};
    const int required = MultiByteToWideChar(CP_ACP, 0, value, -1, nullptr, 0);
    if (required <= 0) return {};
    std::wstring output(static_cast<size_t>(required), L'\0');
    const int written =
        MultiByteToWideChar(CP_ACP, 0, value, -1, output.data(), required);
    if (written <= 0) return {};
    output.resize(static_cast<size_t>(written - 1));
    return output;
}

std::string WidePathToAnsi(const std::wstring& value) {
    if (value.empty()) return {};
    const int required = WideCharToMultiByte(
        CP_ACP, WC_NO_BEST_FIT_CHARS, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string output(static_cast<size_t>(required), '\0');
    BOOL used_default = FALSE;
    const int written = WideCharToMultiByte(CP_ACP,
                                             WC_NO_BEST_FIT_CHARS,
                                             value.c_str(),
                                             -1,
                                             output.data(),
                                             required,
                                             nullptr,
                                             &used_default);
    if (written <= 0 || used_default) return {};
    output.resize(static_cast<size_t>(written - 1));
    return output;
}

void AppendLog(const std::wstring& message) {
    const std::wstring path = GetEnvironmentString(L"CODEX_TZ_SHIM_LOG");
    if (path.empty()) return;

    HANDLE file = CreateFileW(path.c_str(),
                              FILE_APPEND_DATA,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr,
                              OPEN_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL,
                              nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME utc{};
    GetSystemTime(&utc);
    wchar_t prefix[256]{};
    swprintf_s(prefix,
               L"%04u-%02u-%02uT%02u:%02u:%02uZ pid=%lu ",
               utc.wYear,
               utc.wMonth,
               utc.wDay,
               utc.wHour,
               utc.wMinute,
               utc.wSecond,
               GetCurrentProcessId());
    const std::wstring line = std::wstring(prefix) + L"exe=\"" + CurrentExecutablePath() +
                              L"\" role=broker " + message + L"\r\n";
    const int utf8_bytes =
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (utf8_bytes > 1) {
        std::string utf8(static_cast<size_t>(utf8_bytes), '\0');
        const int written = WideCharToMultiByte(CP_UTF8,
                                                 0,
                                                 line.c_str(),
                                                 -1,
                                                 utf8.data(),
                                                 utf8_bytes,
                                                 nullptr,
                                                 nullptr);
        if (written > 1) {
            DWORD ignored = 0;
            WriteFile(file,
                      utf8.data(),
                      static_cast<DWORD>(written - 1),
                      &ignored,
                      nullptr);
        }
    }
    CloseHandle(file);
}

template <typename T>
T ResolveExport(const wchar_t* module_name, const char* proc_name, T fallback) {
    HMODULE module = GetModuleHandleW(module_name);
    if (module == nullptr) module = LoadLibraryW(module_name);
    if (module == nullptr) return fallback;
    FARPROC proc = GetProcAddress(module, proc_name);
    return proc == nullptr ? fallback : reinterpret_cast<T>(proc);
}

bool IsBoundary(wchar_t ch) {
    return ch == L'\0' || iswspace(ch) || ch == L'\"' || ch == L'\'' || ch == L'=' ||
           ch == L':';
}

bool ContainsAppServerToken(const std::wstring& command_line) {
    const std::wstring lower = ToLower(command_line);
    const std::wstring token = L"app-server";
    size_t position = 0;
    while ((position = lower.find(token, position)) != std::wstring::npos) {
        const wchar_t before = position == 0 ? L'\0' : lower[position - 1];
        const size_t after_index = position + token.size();
        const wchar_t after = after_index >= lower.size() ? L'\0' : lower[after_index];
        if (IsBoundary(before) && IsBoundary(after)) return true;
        position += token.size();
    }
    return false;
}

bool IsCodexAppServer(const std::wstring& application_name,
                      const std::wstring& command_line) {
    std::wstring executable = application_name;
    if (executable.empty()) executable = FirstCommandToken(command_line);
    return BaseNameLower(executable) == L"codex.exe" && ContainsAppServerToken(command_line);
}

BOOL WINAPI HookCreateProcessW(LPCWSTR application_name,
                               LPWSTR command_line,
                               LPSECURITY_ATTRIBUTES process_attributes,
                               LPSECURITY_ATTRIBUTES thread_attributes,
                               BOOL inherit_handles,
                               DWORD creation_flags,
                               LPVOID environment,
                               LPCWSTR current_directory,
                               LPSTARTUPINFOW startup_info,
                               LPPROCESS_INFORMATION process_information) {
    const std::wstring app = application_name == nullptr ? L"" : application_name;
    const std::wstring command = command_line == nullptr ? L"" : command_line;
    if (g_timezone_shim_path_ansi.empty() || !IsCodexAppServer(app, command)) {
        return RealCreateProcessW(application_name,
                                  command_line,
                                  process_attributes,
                                  thread_attributes,
                                  inherit_handles,
                                  creation_flags,
                                  environment,
                                  current_directory,
                                  startup_info,
                                  process_information);
    }

    AppendLog(L"match=codex-app-server encoding=wide");
    const BOOL result = DetourCreateProcessWithDllExW(application_name,
                                                       command_line,
                                                       process_attributes,
                                                       thread_attributes,
                                                       inherit_handles,
                                                       creation_flags,
                                                       environment,
                                                       current_directory,
                                                       startup_info,
                                                       process_information,
                                                       g_timezone_shim_path_ansi.c_str(),
                                                       RealCreateProcessW);
    if (result) {
        const DWORD pid = process_information == nullptr ? 0 : process_information->dwProcessId;
        AppendLog(L"injection=success child-pid=" + std::to_wstring(pid));
    } else {
        AppendLog(L"injection=failed error=" + std::to_wstring(GetLastError()));
    }
    return result;
}

BOOL WINAPI HookCreateProcessA(LPCSTR application_name,
                               LPSTR command_line,
                               LPSECURITY_ATTRIBUTES process_attributes,
                               LPSECURITY_ATTRIBUTES thread_attributes,
                               BOOL inherit_handles,
                               DWORD creation_flags,
                               LPVOID environment,
                               LPCSTR current_directory,
                               LPSTARTUPINFOA startup_info,
                               LPPROCESS_INFORMATION process_information) {
    const std::wstring app = AnsiToWide(application_name);
    const std::wstring command = AnsiToWide(command_line);
    if (g_timezone_shim_path_ansi.empty() || !IsCodexAppServer(app, command)) {
        return RealCreateProcessA(application_name,
                                  command_line,
                                  process_attributes,
                                  thread_attributes,
                                  inherit_handles,
                                  creation_flags,
                                  environment,
                                  current_directory,
                                  startup_info,
                                  process_information);
    }

    AppendLog(L"match=codex-app-server encoding=ansi");
    const BOOL result = DetourCreateProcessWithDllExA(application_name,
                                                       command_line,
                                                       process_attributes,
                                                       thread_attributes,
                                                       inherit_handles,
                                                       creation_flags,
                                                       environment,
                                                       current_directory,
                                                       startup_info,
                                                       process_information,
                                                       g_timezone_shim_path_ansi.c_str(),
                                                       RealCreateProcessA);
    if (result) {
        const DWORD pid = process_information == nullptr ? 0 : process_information->dwProcessId;
        AppendLog(L"injection=success child-pid=" + std::to_wstring(pid));
    } else {
        AppendLog(L"injection=failed error=" + std::to_wstring(GetLastError()));
    }
    return result;
}

void AttachHooks() {
    RealCreateProcessW = ResolveExport(L"KernelBase.dll", "CreateProcessW", RealCreateProcessW);
    RealCreateProcessA = ResolveExport(L"KernelBase.dll", "CreateProcessA", RealCreateProcessA);

    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    DetourAttach(&(PVOID&)RealCreateProcessW, HookCreateProcessW);
    DetourAttach(&(PVOID&)RealCreateProcessA, HookCreateProcessA);
    const LONG result = DetourTransactionCommit();
    AppendLog(L"hooks=attach result=" + std::to_wstring(result));
}

void DetachHooks() {
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    DetourDetach(&(PVOID&)RealCreateProcessW, HookCreateProcessW);
    DetourDetach(&(PVOID&)RealCreateProcessA, HookCreateProcessA);
    DetourTransactionCommit();
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (DetourIsHelperProcess()) return TRUE;

    if (reason == DLL_PROCESS_ATTACH) {
        DetourRestoreAfterWith();
        DisableThreadLibraryCalls(instance);

        std::wstring broker_path = ModulePath(instance);
        const size_t separator = broker_path.find_last_of(L"\\/");
        if (separator != std::wstring::npos) broker_path.resize(separator + 1);
        broker_path += L"CodexTzShim64.dll";

        if (GetFileAttributesW(broker_path.c_str()) == INVALID_FILE_ATTRIBUTES) {
            AppendLog(L"timezone-shim=missing path=\"" + broker_path + L"\"");
            return TRUE;
        }
        g_timezone_shim_path_ansi = WidePathToAnsi(broker_path);
        if (g_timezone_shim_path_ansi.empty()) {
            AppendLog(L"timezone-shim=ansi-path-error");
            return TRUE;
        }

        AppendLog(L"loaded timezone-shim=\"" + broker_path + L"\"");
        AttachHooks();
    } else if (reason == DLL_PROCESS_DETACH && !g_timezone_shim_path_ansi.empty()) {
        DetachHooks();
    }
    return TRUE;
}
