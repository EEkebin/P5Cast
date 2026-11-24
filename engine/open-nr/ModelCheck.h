#pragma once
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cmath>
#include <filesystem>
#include <map>
#include <memory>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>
#include "json.h"

namespace p5_open_nr {
using LockedFile = std::unique_ptr<void, decltype(&CloseHandle)>;
inline LockedFile LockFile(const std::filesystem::path& path, bool directory) {
    HANDLE file = CreateFileW(path.c_str(), directory ? FILE_READ_ATTRIBUTES : GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0), nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot read the OpenDLSS-NR model folder or one of its files.");
    LockedFile lock(file, &CloseHandle);
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file, &info) || GetFileType(file) != FILE_TYPE_DISK ||
        ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory ||
        (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
        throw std::runtime_error("Choose a model folder containing regular files.");
    return lock;
}
inline size_t FileSize(HANDLE file, size_t limit) {
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || static_cast<uint64_t>(size.QuadPart) > limit)
        throw std::runtime_error("Invalid OpenDLSS-NR model file size.");
    return static_cast<size_t>(size.QuadPart);
}
inline uint32_t Integer(const json::Value& value) {
    if (value.kind != json::Value::Number || !std::isfinite(value.number) || value.number < 0 ||
        value.number > UINT32_MAX || value.number != std::floor(value.number))
        throw std::runtime_error("Invalid OpenDLSS-NR model dimensions.");
    return static_cast<uint32_t>(value.number);
}
inline void CheckJsonEnvelope(const std::string& text) {
    // Keep the upstream recursive reader within the depth of a model manifest.
    int depth = 0;
    bool quoted = false;
    for (size_t i = 0; i < text.size(); ++i) {
        char c = text[i];
        if (quoted && c == '\\') {
            if (++i >= text.size() || std::string("\"\\/bfnrtu").find(text[i]) == std::string::npos)
                throw std::runtime_error("Invalid escape in model manifest.");
            if (text[i] == 'u') {
                for (int digit = 0; digit < 4; ++digit)
                    if (++i >= text.size() || !std::isxdigit(static_cast<unsigned char>(text[i])))
                        throw std::runtime_error("Invalid Unicode escape in model manifest.");
            }
        } else if (c == '"') quoted = !quoted;
        else if (!quoted && (c == '{' || c == '[')) {
            if (++depth > 32) throw std::runtime_error("Model manifest nesting is too deep.");
        } else if (!quoted && (c == '}' || c == ']')) {
            if (--depth < 0) throw std::runtime_error("Unbalanced model manifest.");
        }
    }
    if (quoted || depth != 0) throw std::runtime_error("Incomplete model manifest.");
}
}

// Keep the files open without write/delete sharing until the upstream loader has finished.
inline std::vector<p5_open_nr::LockedFile> ValidateOpenNrModel(const std::filesystem::path& directory) {
    using namespace p5_open_nr;
    static const std::map<std::string,uint32_t> layout = {
#include "ModelLayout.inc"
    };
    std::vector<LockedFile> locks;
    locks.push_back(LockFile(directory, true));
    locks.push_back(LockFile(directory / L"model", true));
    locks.push_back(LockFile(directory / L"manifest.json", false));
    HANDLE manifest = locks.back().get();
    std::string text(FileSize(manifest, 1024 * 1024), '\0');
    DWORD count = 0;
    if (!ReadFile(manifest, text.data(), static_cast<DWORD>(text.size()), &count, nullptr) || count != text.size())
        throw std::runtime_error("Cannot read the OpenDLSS-NR model manifest.");
    CheckJsonEnvelope(text);
    const auto root = json::parse(text);
    const auto& stages = root["stages"].array;
    const auto& tensors = root["tensors"].array;
    if (Integer(root["totals"]["blockCount"]) != 71 || tensors.size() != layout.size() ||
        stages.empty() || stages.size() > tensors.size())
        throw std::runtime_error("Choose the OpenDLSS-NR 310.8.0 full model (71 blocks and 153 tensors).");
    std::map<std::string,uint32_t> sizes;
    uint64_t totalBytes = 0;
    for (const auto& stage : stages) {
        const std::string& file = stage["file"].str();
        const std::string& id = stage["id"].str();
        const auto size = Integer(stage["packedByteLength"]);
        if (file.empty() || file.size() > 240 || file == "." || file == ".." ||
            file.find_first_of("\\/:*?\"<>|") != std::string::npos ||
            std::any_of(file.begin(), file.end(), [](unsigned char c) { return c < 32; }) ||
            file.back() == '.' || file.back() == ' ' || id.empty() || !sizes.emplace(id, size).second)
            throw std::runtime_error("Invalid stage identifier or file name in the OpenDLSS-NR model.");
        const auto& hash = stage["sha256"].str();
        if (hash.size() != 64 || hash.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos)
            throw std::runtime_error("Missing or invalid OpenDLSS-NR stage checksum.");
        totalBytes += size;
        if (totalBytes > 256u * 1024 * 1024) throw std::runtime_error("OpenDLSS-NR stage data exceeds the full model size limit.");
        locks.push_back(LockFile(directory / L"model" / std::filesystem::u8path(file), false));
        if (FileSize(locks.back().get(), size) != size) throw std::runtime_error("OpenDLSS-NR model stage is truncated.");
    }
    std::set<std::string> seen;
    std::map<std::string, std::vector<std::pair<uint32_t, uint32_t>>> ranges;
    for (const auto& tensor : tensors) {
        const std::string name = "block" + std::to_string(Integer(tensor["block"])) + ".layer" +
            std::to_string(Integer(tensor["layer"])) + "." + tensor["parameter"].str();
        const auto expected = layout.find(name);
        const auto stage = sizes.find(tensor["stage"].str());
        const auto length = Integer(tensor["byteLength"]);
        const auto offset = Integer(tensor["stageOffset"]);
        if (expected == layout.end() || name != tensor["name"].str() || length != expected->second ||
            !seen.insert(name).second || stage == sizes.end() ||
            uint64_t(offset) + length > stage->second)
            throw std::runtime_error("Tensor layout does not match the 310.8.0 full OpenDLSS-NR network.");
        ranges[stage->first].emplace_back(offset, offset + length);
    }
    for (auto& stage : ranges) {
        auto& spans = stage.second;
        std::sort(spans.begin(), spans.end());
        for (size_t i = 1; i < spans.size(); ++i)
            if (spans[i].first < spans[i - 1].second)
                throw std::runtime_error("Overlapping tensor data in the OpenDLSS-NR model.");
    }
    return locks;
}
