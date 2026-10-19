#include <windows.h>
#include <shellapi.h>

#include <string>
#include <vector>

namespace
{
std::wstring QuoteArgument(const std::wstring& value)
{
    if (value.find_first_of(L" \t\n\v\"") == std::wstring::npos)
        return value;

    std::wstring quoted = L"\"";
    size_t backslashes = 0;
    for (const wchar_t character : value)
    {
        if (character == L'\\')
        {
            ++backslashes;
            continue;
        }

        if (character == L'\"')
        {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted.push_back(character);
            backslashes = 0;
            continue;
        }

        quoted.append(backslashes, L'\\');
        backslashes = 0;
        quoted.push_back(character);
    }

    quoted.append(backslashes * 2, L'\\');
    quoted.push_back(L'\"');
    return quoted;
}

std::wstring GetLauncherDirectory()
{
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
        return {};

    std::wstring path(buffer.data(), length);
    const size_t separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : path.substr(0, separator);
}

void ShowLaunchError(const std::wstring& message)
{
    MessageBoxW(nullptr, message.c_str(), L"FrameVault V1.0.0", MB_OK | MB_ICONERROR);
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const std::wstring launcherDirectory = GetLauncherDirectory();
    if (launcherDirectory.empty())
    {
        ShowLaunchError(L"프로그램 위치를 확인할 수 없습니다.");
        return 1;
    }

    const std::wstring appDirectory = launcherDirectory + L"\\app";
    const std::wstring appPath = appDirectory + L"\\FrameVault.exe";
    if (GetFileAttributesW(appPath.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        ShowLaunchError(
            L"app\\FrameVault.exe를 찾을 수 없습니다.\n"
            L"ZIP을 완전히 압축 해제하고 app 폴더를 실행 파일과 같은 위치에 두세요.");
        return 2;
    }

    int argumentCount = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    std::wstring commandLine = QuoteArgument(appPath);
    if (arguments != nullptr)
    {
        for (int index = 1; index < argumentCount; ++index)
        {
            commandLine.push_back(L' ');
            commandLine.append(QuoteArgument(arguments[index]));
        }
        LocalFree(arguments);
    }

    std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back(L'\0');

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    PROCESS_INFORMATION processInfo{};
    if (!CreateProcessW(
            appPath.c_str(),
            mutableCommandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            0,
            nullptr,
            appDirectory.c_str(),
            &startupInfo,
            &processInfo))
    {
        const DWORD error = GetLastError();
        ShowLaunchError(L"프로그램을 시작하지 못했습니다. Windows 오류 코드: " + std::to_wstring(error));
        return static_cast<int>(error);
    }

    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);
    return 0;
}

