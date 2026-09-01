#include <windows.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/base.h>

#include <iostream>
#include <string>

namespace {

std::wstring ReadEnvironment(const wchar_t* name) {
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0) return {};
    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required) return {};
    value.resize(written);
    return value;
}

}  // namespace

int wmain() {
    DYNAMIC_TIME_ZONE_INFORMATION dynamic_info{};
    const DWORD state = GetDynamicTimeZoneInformation(&dynamic_info);

    TIME_ZONE_INFORMATION year_info{};
    const BOOL year_ok = GetTimeZoneInformationForYear(2026, nullptr, &year_info);

    SYSTEMTIME local{};
    GetLocalTime(&local);

    std::wcout << L"env.tz=" << ReadEnvironment(L"TZ") << L"\n";
    std::wcout << L"env.iana=" << ReadEnvironment(L"CODEX_TZ_IANA") << L"\n";
    std::wcout << L"win32.key=" << dynamic_info.TimeZoneKeyName << L"\n";
    std::wcout << L"win32.state=" << state << L"\n";
    std::wcout << L"win32.bias=" << dynamic_info.Bias << L"\n";
    std::wcout << L"win32.year_ok=" << year_ok << L"\n";
    std::wcout << L"win32.year_bias=" << year_info.Bias << L"\n";
    std::wcout << L"local=" << local.wYear << L"-" << local.wMonth << L"-" << local.wDay << L" "
               << local.wHour << L":" << local.wMinute << L":" << local.wSecond << L"\n";

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        winrt::Windows::Globalization::Calendar calendar;
        std::wcout << L"winrt.iana=" << calendar.GetTimeZone().c_str() << L"\n";
    } catch (const winrt::hresult_error& error) {
        std::wcout << L"winrt.error=0x" << std::hex << static_cast<unsigned long>(error.code().value)
                   << std::dec << L" " << error.message().c_str() << L"\n";
        return 5;
    }

    return 0;
}
