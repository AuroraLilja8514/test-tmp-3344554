#include <windows.h>
#include <detours.h>

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace {

std::wstring QuoteArgument(const std::wstring& value) {
    if (value.find_first_of(L" \t\"") == std::wstring::npos) {
        return value;
    }
    std::wstring output = L"\"";
    size_t backslashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') {
            ++backslashes;
            continue;
        }
        if (ch == L'\"') {
            output.append(backslashes * 2 + 1, L'\\');
            output.push_back(L'\"');
            backslashes = 0;
            continue;
        }
        output.append(backslashes, L'\\');
        backslashes = 0;
        output.push_back(ch);
    }
    output.append(backslashes * 2, L'\\');
    output.push_back(L'\"');
    return output;
}

std::string WideToAnsi(const std::wstring& value) {
    const int required = WideCharToMultiByte(
        CP_ACP, WC_NO_BEST_FIT_CHARS, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (required <= 0) {
        return {};
    }
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
    if (written <= 0 || used_default) {
        return {};
    }
    output.resize(static_cast<size_t>(written - 1));
    return output;
}

void PrintUsage() {
    std::wcerr << L"Usage: tzshim-launcher.exe --timezone-windows-id <Windows ID> "
                  L"[--log <path>] -- <exe> [args...]\n";
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    std::wstring windows_id;
    std::wstring log_path;
    int separator = -1;

    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--") {
            separator = i;
            break;
        }
        if (arg == L"--timezone-windows-id" && i + 1 < argc) {
            windows_id = argv[++i];
            continue;
        }
        if (arg == L"--log" && i + 1 < argc) {
            log_path = argv[++i];
            continue;
        }
        PrintUsage();
        return 2;
    }

    if (windows_id.empty() || separator < 0 || separator + 1 >= argc) {
        PrintUsage();
        return 2;
    }

    wchar_t own_path[MAX_PATH * 4]{};
    if (GetModuleFileNameW(nullptr, own_path, static_cast<DWORD>(std::size(own_path))) == 0) {
        std::wcerr << L"Unable to determine launcher path. Error " << GetLastError() << L"\n";
        return 3;
    }
    const auto shim_path = std::filesystem::path(own_path).parent_path() / L"CodexTzShim64.dll";
    if (!std::filesystem::exists(shim_path)) {
        std::wcerr << L"Timezone shim DLL not found: " << shim_path.c_str() << L"\n";
        return 3;
    }

    const std::string shim_ansi = WideToAnsi(shim_path.wstring());
    if (shim_ansi.empty()) {
        std::wcerr << L"Shim path cannot be represented in the active Windows ANSI code page.\n";
        return 3;
    }

    SetEnvironmentVariableW(L"CODEX_TZ_WINDOWS_ID", windows_id.c_str());
    if (!log_path.empty()) {
        SetEnvironmentVariableW(L"CODEX_TZ_SHIM_LOG", log_path.c_str());
    }

    const std::wstring executable = argv[separator + 1];
    std::wstring command_line = QuoteArgument(executable);
    for (int i = separator + 2; i < argc; ++i) {
        command_line.push_back(L' ');
        command_line += QuoteArgument(argv[i]);
    }
    std::vector<wchar_t> mutable_command(command_line.begin(), command_line.end());
    mutable_command.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};

    const BOOL created = DetourCreateProcessWithDllExW(executable.c_str(),
                                                        mutable_command.data(),
                                                        nullptr,
                                                        nullptr,
                                                        TRUE,
                                                        0,
                                                        nullptr,
                                                        nullptr,
                                                        &startup,
                                                        &process,
                                                        shim_ansi.c_str(),
                                                        nullptr);
    if (!created) {
        std::wcerr << L"Failed to launch target with timezone shim. Error " << GetLastError() << L"\n";
        return 4;
    }

    std::wcout << L"Started PID " << process.dwProcessId << L" with process-local Windows timezone ID '"
               << windows_id << L"'.\n";
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
