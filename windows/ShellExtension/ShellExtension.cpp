#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <objbase.h>
#include <atomic>
#include <algorithm>
#include <new>
#include <string>
#include <vector>
#include <sstream>
#include <iomanip>

// This CLSID is also declared in the MSIX manifest.
const CLSID CLSID_LocalBridgeCommand =
{ 0x5e932c6e, 0x8f1f, 0x4d3a, { 0xad, 0x2f, 0x1c, 0x7d, 0xfb, 0xd8, 0x90, 0xa1 } };

namespace
{
    std::atomic<long> g_moduleReferences = 0;
    HMODULE g_module = nullptr;

    std::wstring ModuleDirectory()
    {
        // The shell DLL and agent ship together. A saved InstallPath can be
        // absent on first use or point at a removed version after an MSIX update.
        std::wstring path(32768, L'\0');
        const DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0 || length >= path.size()) return {};
        path.resize(length);
        const auto separator = path.find_last_of(L"\\/");
        if (separator == std::wstring::npos) return {};
        path.resize(separator);
        return path;
    }

    std::wstring ReadRegistryString(const wchar_t* valueName, const wchar_t* fallback)
    {
        wchar_t buffer[1024]{};
        DWORD bytes = sizeof(buffer);
        DWORD type = 0;
        const LSTATUS result = RegGetValueW(
            HKEY_CURRENT_USER,
            L"Software\\LocalBridge",
            valueName,
            RRF_RT_REG_SZ,
            &type,
            buffer,
            &bytes);
        return result == ERROR_SUCCESS && buffer[0] != L'\0' ? buffer : fallback;
    }

    std::string Utf8(const std::wstring& value)
    {
        if (value.empty()) return {};
        const int required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (required <= 0) return {};
        std::string output(static_cast<size_t>(required), '\0');
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), output.data(), required, nullptr, nullptr) <= 0)
            return {};
        return output;
    }

    std::string JsonEscape(const std::string& value)
    {
        std::ostringstream output;
        for (const unsigned char character : value)
        {
            switch (character)
            {
            case '\\': output << "\\\\"; break;
            case '"': output << "\\\""; break;
            case '\b': output << "\\b"; break;
            case '\f': output << "\\f"; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default:
                if (character < 0x20)
                    output << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(character);
                else
                    output << character;
                break;
            }
        }
        return output.str();
    }

    std::string CreateRequestId()
    {
        GUID id{};
        if (FAILED(CoCreateGuid(&id))) return "shell-request";
        wchar_t text[40]{};
        StringFromGUID2(id, text, ARRAYSIZE(text));
        std::wstring value(text);
        value.erase(std::remove_if(value.begin(), value.end(), [](wchar_t c) { return c == L'{' || c == L'}' || c == L'-'; }), value.end());
        return Utf8(value);
    }

    std::string UtcTimestamp()
    {
        SYSTEMTIME time{};
        GetSystemTime(&time);
        char text[40]{};
        sprintf_s(text, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        return text;
    }

    std::string BuildRequest(const std::vector<std::wstring>& paths)
    {
        std::ostringstream json;
        json << "{\"paths\":[";
        for (size_t index = 0; index < paths.size(); ++index)
        {
            if (index != 0) json << ',';
            json << '"' << JsonEscape(Utf8(paths[index])) << '"';
        }
        json << "],\"requestId\":\"" << CreateRequestId()
             << "\",\"createdAt\":\"" << UtcTimestamp() << "\"}";
        return json.str();
    }

    bool SendToPipe(const std::string& request)
    {
        HANDLE pipe = CreateFileW(
            L"\\\\.\\pipe\\LocalBridgeSender",
            GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return false;
        const DWORD size = static_cast<DWORD>(request.size());
        DWORD written = 0;
        const bool ok = WriteFile(pipe, &size, sizeof(size), &written, nullptr) && written == sizeof(size)
            && WriteFile(pipe, request.data(), size, &written, nullptr) && written == size;
        CloseHandle(pipe);
        return ok;
    }

    std::wstring PendingDirectory()
    {
        wchar_t localAppData[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, ARRAYSIZE(localAppData));
        if (length == 0 || length >= ARRAYSIZE(localAppData)) return {};
        std::wstring directory(localAppData);
        directory += L"\\LocalBridge\\Pending";
        CreateDirectoryW((std::wstring(localAppData) + L"\\LocalBridge").c_str(), nullptr);
        CreateDirectoryW(directory.c_str(), nullptr);
        return directory;
    }

    bool WritePendingAndStartAgent(const std::string& request)
    {
        const std::wstring directory = PendingDirectory();
        const std::wstring installPath = ModuleDirectory();
        if (directory.empty() || installPath.empty()) return false;
        const std::string requestId = CreateRequestId();
        const std::wstring requestIdWide(requestId.begin(), requestId.end());
        const std::wstring requestPath = directory + L"\\" + requestIdWide + L".json";
        HANDLE file = CreateFileW(requestPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        DWORD written = 0;
        const bool wrote = WriteFile(file, request.data(), static_cast<DWORD>(request.size()), &written, nullptr) && written == request.size();
        FlushFileBuffers(file);
        CloseHandle(file);
        if (!wrote)
        {
            DeleteFileW(requestPath.c_str());
            return false;
        }

        const std::wstring agent = installPath + L"\\LocalBridge.Agent.exe";
        std::wstring commandLine = L"\"" + agent + L"\" --request-file \"" + requestPath + L"\"";
        STARTUPINFOW startup{ sizeof(startup) };
        PROCESS_INFORMATION process{};
        const BOOL started = CreateProcessW(agent.c_str(), commandLine.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, installPath.c_str(), &startup, &process);
        if (started)
        {
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return true;
        }
        return false;
    }

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        ExplorerCommand() : references_(1) { ++g_moduleReferences; }
        ~ExplorerCommand() { --g_moduleReferences; }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(IExplorerCommand)) *object = static_cast<IExplorerCommand*>(this);
            if (!*object) return E_NOINTERFACE;
            AddRef();
            return S_OK;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG value = static_cast<ULONG>(--references_);
            if (value == 0) delete this;
            return value;
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override
        {
            if (!title) return E_POINTER;
            return SHStrDupW(ReadRegistryString(L"MenuTitle", L"iPhoneに送る").c_str(), title);
        }
        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
        {
            if (!icon) return E_POINTER;
            *icon = nullptr;
            const std::wstring installPath = ModuleDirectory();
            if (installPath.empty()) return E_NOTIMPL;
            return SHStrDupW((installPath + L"\\LocalBridge.Settings.exe,0").c_str(), icon);
        }
        IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* toolTip) override
        {
            if (!toolTip) return E_POINTER;
            return SHStrDupW(L"登録済みのiPhoneへ同じWi-Fiで送信します", toolTip);
        }
        IFACEMETHODIMP GetCanonicalName(GUID* guid) override
        {
            if (!guid) return E_POINTER;
            *guid = CLSID_LocalBridgeCommand;
            return S_OK;
        }
        IFACEMETHODIMP GetState(IShellItemArray* selection, BOOL, EXPCMDSTATE* state) override
        {
            if (!state) return E_POINTER;
            DWORD count = 0;
            *state = selection && SUCCEEDED(selection->GetCount(&count)) && count > 0 ? ECS_ENABLED : ECS_DISABLED;
            return S_OK;
        }
        IFACEMETHODIMP Invoke(IShellItemArray* selection, IBindCtx*) noexcept override
        {
            if (!selection) return E_INVALIDARG;
            DWORD count = 0;
            if (FAILED(selection->GetCount(&count)) || count == 0 || count > 256) return E_INVALIDARG;
            std::vector<std::wstring> paths;
            paths.reserve(count);
            for (DWORD index = 0; index < count; ++index)
            {
                IShellItem* item = nullptr;
                if (SUCCEEDED(selection->GetItemAt(index, &item)))
                {
                    PWSTR path = nullptr;
                    if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
                    {
                        paths.emplace_back(path);
                        CoTaskMemFree(path);
                    }
                    item->Release();
                }
            }
            if (paths.empty()) return E_INVALIDARG;
            const std::string request = BuildRequest(paths);
            if (request.size() > 1024 * 1024) return HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE);
            return SendToPipe(request) || WritePendingAndStartAgent(request) ? S_OK : HRESULT_FROM_WIN32(ERROR_PIPE_NOT_CONNECTED);
        }
        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (!flags) return E_POINTER;
            *flags = ECF_DEFAULT;
            return S_OK;
        }
        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            if (commands) *commands = nullptr;
            return E_NOTIMPL;
        }

    private:
        std::atomic<long> references_;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() : references_(1) { ++g_moduleReferences; }
        ~ClassFactory() { --g_moduleReferences; }
        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (iid == IID_IUnknown || iid == IID_IClassFactory) *object = static_cast<IClassFactory*>(this);
            if (!*object) return E_NOINTERFACE;
            AddRef();
            return S_OK;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(++references_); }
        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG value = static_cast<ULONG>(--references_);
            if (value == 0) delete this;
            return value;
        }
        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** object) override
        {
            if (outer) return CLASS_E_NOAGGREGATION;
            auto* command = new (std::nothrow) ExplorerCommand();
            if (!command) return E_OUTOFMEMORY;
            const HRESULT result = command->QueryInterface(iid, object);
            command->Release();
            return result;
        }
        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock) ++g_moduleReferences; else --g_moduleReferences;
            return S_OK;
        }
    private:
        std::atomic<long> references_;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return g_moduleReferences.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (clsid != CLSID_LocalBridgeCommand) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}
