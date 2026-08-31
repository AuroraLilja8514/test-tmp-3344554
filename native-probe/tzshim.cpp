#include <windows.h>
#include <detours.h>

#include <algorithm>
#include <cwchar>
#include <string>
#include <vector>

namespace {

DYNAMIC_TIME_ZONE_INFORMATION g_target_zone{};
bool g_target_loaded = false;
std::string g_dll_path_ansi;

static decltype(&GetDynamicTimeZoneInformation) RealGetDynamicTimeZoneInformation =
    GetDynamicTimeZoneInformation;
static decltype(&GetTimeZoneInformation) RealGetTimeZoneInformation = GetTimeZoneInformation;
static decltype(&GetTimeZoneInformationForYear) RealGetTimeZoneInformationForYear =
    GetTimeZoneInformationForYear;
static decltype(&GetLocalTime) RealGetLocalTime = GetLocalTime;
static decltype(&SystemTimeToTzSpecificLocalTimeEx) RealSystemTimeToTzSpecificLocalTimeEx =
    SystemTimeToTzSpecificLocalTimeEx;
static decltype(&TzSpecificLocalTimeToSystemTimeEx) RealTzSpecificLocalTimeToSystemTimeEx =
    TzSpecificLocalTimeToSystemTimeEx;
static decltype(&SystemTimeToTzSpecificLocalTime) RealSystemTimeToTzSpecificLocalTime =
    SystemTimeToTzSpecificLocalTime;
static decltype(&TzSpecificLocalTimeToSystemTime) RealTzSpecificLocalTimeToSystemTime =
    TzSpecificLocalTimeToSystemTime;
static decltype(&CreateProcessW) RealCreateProcessW = CreateProcessW;
static decltype(&CreateProcessA) RealCreateProcessA = CreateProcessA;

std::wstring GetEnvironmentString(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) {
        return {};
    }
    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) {
        return {};
    }
    value.resize(written);
    return value;
}

std::string WidePathToAnsi(const std::wstring& value) {
    if (value.empty()) {
        return {};
    }
    const int required = WideCharToMultiByte(
        CP_ACP, WC_NO_BEST_FIT_CHARS, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (required <= 0) {
        return {};
    }
    std::string output(static_cast<size_t>(required), '\0');
    BOOL used_default = FALSE;
    if (WideCharToMultiByte(CP_ACP,
                            WC_NO_BEST_FIT_CHARS,
                            value.c_str(),
                            -1,
                            output.data(),
                            required,
                            nullptr,
                            &used_default) <= 0 ||
        used_default) {
        return {};
    }
    output.resize(static_cast<size_t>(required - 1));
    return output;
}

void AppendLog(const std::wstring& message) {
    const std::wstring path = GetEnvironmentString(L"CODEX_TZ_SHIM_LOG");
    if (path.empty()) {
        return;
    }
    HANDLE file = CreateFileW(path.c_str(),
                              FILE_APPEND_DATA,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr,
                              OPEN_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL,
                              nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }

    SYSTEMTIME utc{};
    GetSystemTime(&utc);
    wchar_t exe_path[MAX_PATH * 4]{};
    GetModuleFileNameW(nullptr, exe_path, static_cast<DWORD>(std::size(exe_path)));

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
    const std::wstring line = std::wstring(prefix) + L"exe=\"" + exe_path + L"\" " + message + L"\r\n";
    const int utf8_bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (utf8_bytes > 1) {
        std::string utf8(static_cast<size_t>(utf8_bytes - 1), '\0');
        WideCharToMultiByte(CP_UTF8,
                            0,
                            line.c_str(),
                            -1,
                            utf8.data(),
                            utf8_bytes,
                            nullptr,
                            nullptr);
        DWORD ignored = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &ignored, nullptr);
    }
    CloseHandle(file);
}

bool LoadTargetZone() {
    const std::wstring requested = GetEnvironmentString(L"CODEX_TZ_WINDOWS_ID");
    if (requested.empty()) {
        return false;
    }

    for (DWORD index = 0;; ++index) {
        DYNAMIC_TIME_ZONE_INFORMATION candidate{};
        const DWORD result = EnumDynamicTimeZoneInformation(index, &candidate);
        if (result == ERROR_NO_MORE_ITEMS) {
            break;
        }
        if (result != ERROR_SUCCESS) {
            return false;
        }
        if (_wcsicmp(candidate.TimeZoneKeyName, requested.c_str()) == 0) {
            g_target_zone = candidate;
            g_target_loaded = true;
            return true;
        }
    }
    return false;
}

DWORD CurrentTargetState() {
    if (!g_target_loaded) {
        return TIME_ZONE_ID_INVALID;
    }

    SYSTEMTIME utc{};
    SYSTEMTIME local{};
    GetSystemTime(&utc);
    if (!RealSystemTimeToTzSpecificLocalTimeEx(&g_target_zone, &utc, &local)) {
        return TIME_ZONE_ID_UNKNOWN;
    }

    TIME_ZONE_INFORMATION info{};
    if (!RealGetTimeZoneInformationForYear(utc.wYear, &g_target_zone, &info)) {
        return TIME_ZONE_ID_UNKNOWN;
    }

    FILETIME utc_file{};
    FILETIME local_file{};
    if (!SystemTimeToFileTime(&utc, &utc_file) || !SystemTimeToFileTime(&local, &local_file)) {
        return TIME_ZONE_ID_UNKNOWN;
    }

    ULARGE_INTEGER utc_value{};
    utc_value.LowPart = utc_file.dwLowDateTime;
    utc_value.HighPart = utc_file.dwHighDateTime;
    ULARGE_INTEGER local_value{};
    local_value.LowPart = local_file.dwLowDateTime;
    local_value.HighPart = local_file.dwHighDateTime;

    const LONGLONG ticks_per_minute = 60LL * 10'000'000LL;
    const LONGLONG difference =
        static_cast<LONGLONG>(local_value.QuadPart) - static_cast<LONGLONG>(utc_value.QuadPart);
    const LONG offset_minutes = static_cast<LONG>(difference / ticks_per_minute);
    const LONG standard_minutes = -(info.Bias + info.StandardBias);
    const LONG daylight_minutes = -(info.Bias + info.DaylightBias);

    if (info.DaylightDate.wMonth != 0 && offset_minutes == daylight_minutes &&
        daylight_minutes != standard_minutes) {
        return TIME_ZONE_ID_DAYLIGHT;
    }
    if (offset_minutes == standard_minutes) {
        return TIME_ZONE_ID_STANDARD;
    }
    return TIME_ZONE_ID_UNKNOWN;
}

DWORD WINAPI HookGetDynamicTimeZoneInformation(PDYNAMIC_TIME_ZONE_INFORMATION output) {
    if (!g_target_loaded || output == nullptr) {
        return RealGetDynamicTimeZoneInformation(output);
    }
    *output = g_target_zone;
    return CurrentTargetState();
}

DWORD WINAPI HookGetTimeZoneInformation(LPTIME_ZONE_INFORMATION output) {
    if (!g_target_loaded || output == nullptr) {
        return RealGetTimeZoneInformation(output);
    }
    SYSTEMTIME utc{};
    GetSystemTime(&utc);
    if (!RealGetTimeZoneInformationForYear(utc.wYear, &g_target_zone, output)) {
        return TIME_ZONE_ID_INVALID;
    }
    return CurrentTargetState();
}

BOOL WINAPI HookGetTimeZoneInformationForYear(USHORT year,
                                               PDYNAMIC_TIME_ZONE_INFORMATION zone,
                                               LPTIME_ZONE_INFORMATION output) {
    if (!g_target_loaded || zone != nullptr) {
        return RealGetTimeZoneInformationForYear(year, zone, output);
    }
    return RealGetTimeZoneInformationForYear(year, &g_target_zone, output);
}

VOID WINAPI HookGetLocalTime(LPSYSTEMTIME output) {
    if (!g_target_loaded || output == nullptr) {
        RealGetLocalTime(output);
        return;
    }
    SYSTEMTIME utc{};
    GetSystemTime(&utc);
    if (!RealSystemTimeToTzSpecificLocalTimeEx(&g_target_zone, &utc, output)) {
        RealGetLocalTime(output);
    }
}

BOOL WINAPI HookSystemTimeToTzSpecificLocalTimeEx(const DYNAMIC_TIME_ZONE_INFORMATION* zone,
                                                   const SYSTEMTIME* universal,
                                                   LPSYSTEMTIME local) {
    if (g_target_loaded && zone == nullptr) {
        zone = &g_target_zone;
    }
    return RealSystemTimeToTzSpecificLocalTimeEx(zone, universal, local);
}

BOOL WINAPI HookTzSpecificLocalTimeToSystemTimeEx(const DYNAMIC_TIME_ZONE_INFORMATION* zone,
                                                   const SYSTEMTIME* local,
                                                   LPSYSTEMTIME universal) {
    if (g_target_loaded && zone == nullptr) {
        zone = &g_target_zone;
    }
    return RealTzSpecificLocalTimeToSystemTimeEx(zone, local, universal);
}

BOOL WINAPI HookSystemTimeToTzSpecificLocalTime(const TIME_ZONE_INFORMATION* zone,
                                                 const SYSTEMTIME* universal,
                                                 LPSYSTEMTIME local) {
    TIME_ZONE_INFORMATION target{};
    if (g_target_loaded && zone == nullptr && universal != nullptr) {
        const USHORT year = universal->wYear == 0 ? 2026 : universal->wYear;
        if (RealGetTimeZoneInformationForYear(year, &g_target_zone, &target)) {
            zone = &target;
        }
    }
    return RealSystemTimeToTzSpecificLocalTime(zone, universal, local);
}

BOOL WINAPI HookTzSpecificLocalTimeToSystemTime(const TIME_ZONE_INFORMATION* zone,
                                                 const SYSTEMTIME* local,
                                                 LPSYSTEMTIME universal) {
    TIME_ZONE_INFORMATION target{};
    if (g_target_loaded && zone == nullptr && local != nullptr) {
        const USHORT year = local->wYear == 0 ? 2026 : local->wYear;
        if (RealGetTimeZoneInformationForYear(year, &g_target_zone, &target)) {
            zone = &target;
        }
    }
    return RealTzSpecificLocalTimeToSystemTime(zone, local, universal);
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
    if (g_dll_path_ansi.empty()) {
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
                                                       g_dll_path_ansi.c_str(),
                                                       RealCreateProcessW);
    if (!result) {
        AppendLog(L"child-injection=failed error=" + std::to_wstring(GetLastError()));
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
    if (g_dll_path_ansi.empty()) {
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
                                                       g_dll_path_ansi.c_str(),
                                                       RealCreateProcessA);
    if (!result) {
        AppendLog(L"child-injection=failed error=" + std::to_wstring(GetLastError()));
    }
    return result;
}

void AttachHooks() {
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    DetourAttach(&(PVOID&)RealGetDynamicTimeZoneInformation, HookGetDynamicTimeZoneInformation);
    DetourAttach(&(PVOID&)RealGetTimeZoneInformation, HookGetTimeZoneInformation);
    DetourAttach(&(PVOID&)RealGetTimeZoneInformationForYear, HookGetTimeZoneInformationForYear);
    DetourAttach(&(PVOID&)RealGetLocalTime, HookGetLocalTime);
    DetourAttach(&(PVOID&)RealSystemTimeToTzSpecificLocalTimeEx, HookSystemTimeToTzSpecificLocalTimeEx);
    DetourAttach(&(PVOID&)RealTzSpecificLocalTimeToSystemTimeEx, HookTzSpecificLocalTimeToSystemTimeEx);
    DetourAttach(&(PVOID&)RealSystemTimeToTzSpecificLocalTime, HookSystemTimeToTzSpecificLocalTime);
    DetourAttach(&(PVOID&)RealTzSpecificLocalTimeToSystemTime, HookTzSpecificLocalTimeToSystemTime);
    DetourAttach(&(PVOID&)RealCreateProcessW, HookCreateProcessW);
    DetourAttach(&(PVOID&)RealCreateProcessA, HookCreateProcessA);
    const LONG error = DetourTransactionCommit();
    AppendLog(L"hooks=attach result=" + std::to_wstring(error));
}

void DetachHooks() {
    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    DetourDetach(&(PVOID&)RealGetDynamicTimeZoneInformation, HookGetDynamicTimeZoneInformation);
    DetourDetach(&(PVOID&)RealGetTimeZoneInformation, HookGetTimeZoneInformation);
    DetourDetach(&(PVOID&)RealGetTimeZoneInformationForYear, HookGetTimeZoneInformationForYear);
    DetourDetach(&(PVOID&)RealGetLocalTime, HookGetLocalTime);
    DetourDetach(&(PVOID&)RealSystemTimeToTzSpecificLocalTimeEx, HookSystemTimeToTzSpecificLocalTimeEx);
    DetourDetach(&(PVOID&)RealTzSpecificLocalTimeToSystemTimeEx, HookTzSpecificLocalTimeToSystemTimeEx);
    DetourDetach(&(PVOID&)RealSystemTimeToTzSpecificLocalTime, HookSystemTimeToTzSpecificLocalTime);
    DetourDetach(&(PVOID&)RealTzSpecificLocalTimeToSystemTime, HookTzSpecificLocalTimeToSystemTime);
    DetourDetach(&(PVOID&)RealCreateProcessW, HookCreateProcessW);
    DetourDetach(&(PVOID&)RealCreateProcessA, HookCreateProcessA);
    DetourTransactionCommit();
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (DetourIsHelperProcess()) {
        return TRUE;
    }

    if (reason == DLL_PROCESS_ATTACH) {
        DetourRestoreAfterWith();
        DisableThreadLibraryCalls(instance);

        wchar_t dll_path[MAX_PATH * 4]{};
        if (GetModuleFileNameW(instance, dll_path, static_cast<DWORD>(std::size(dll_path))) != 0) {
            g_dll_path_ansi = WidePathToAnsi(dll_path);
        }

        const bool loaded = LoadTargetZone();
        AppendLog(loaded ? (L"target=loaded windows-id=\"" +
                            std::wstring(g_target_zone.TimeZoneKeyName) + L"\"")
                         : L"target=not-loaded");
        if (loaded) {
            AttachHooks();
        }
    } else if (reason == DLL_PROCESS_DETACH && g_target_loaded) {
        DetachHooks();
    }
    return TRUE;
}
