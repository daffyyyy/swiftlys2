/************************************************************************************************
 *  SwiftlyS2 is a scripting framework for Source2-based games.
 *  Copyright (C) 2023-2026 Swiftly Solution SRL via Sava Andrei-Sebastian and it's contributors
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 ************************************************************************************************/

#ifndef _WIN32

#include "crashreporter.h"
#include "tracer.h"
#ifdef PAGE_SIZE
#undef PAGE_SIZE
#endif
#include <core/managed/host/strconv.h>
#include <core/entrypoint.h>

#include <api/interfaces/manager.h>
#include <api/shared/files.h>
#include <fmt/format.h>

#include <common/path_helper.h>
#include <common/scoped_ptr.h>
#include <google_breakpad/processor/basic_source_line_resolver.h>
#include <google_breakpad/processor/minidump_processor.h>
#include <google_breakpad/processor/process_state.h>
#include <processor/simple_symbol_supplier.h>
#include <processor/stackwalk_common.h>
#include <google_breakpad/processor/call_stack.h>
#include <google_breakpad/processor/stack_frame.h>
#include <processor/pathname_stripper.h>

extern std::string g_dumpPath;
extern std::string g_relativeDumpPath;
extern bool g_dumpWritten;
bool linuxDumpCallback(const google_breakpad::MinidumpDescriptor& descriptor, void* context, bool succeeded);
void TracerDump(const std::string& corePath, const char* path);

void (*SignalHandler)(int, siginfo_t*, void*);
const int kExceptionSignals[] = { SIGSEGV, SIGABRT, SIGFPE, SIGILL, SIGBUS };
const int kNumHandledSignals = std::size(kExceptionSignals);

void InitCrashReporterLinux()
{
    google_breakpad::MinidumpDescriptor descriptor(g_dumpPath);

    exceptionHandler = new google_breakpad::ExceptionHandler(descriptor, NULL, linuxDumpCallback, NULL, true, -1);

    struct sigaction oact;
    sigaction(SIGSEGV, NULL, &oact);
    SignalHandler = oact.sa_sigaction;
}

void CrashReporterOnTickLinux()
{
    bool signalChanged = false;
    struct sigaction oact;

    for (int i = 0; i < kNumHandledSignals; ++i)
    {
        sigaction(kExceptionSignals[i], NULL, &oact);

        if (oact.sa_sigaction != SignalHandler)
        {
            signalChanged = true;
            break;
        }
    }

    if (!signalChanged)
        return;

    struct sigaction act;
    memset(&act, 0, sizeof(act));
    sigemptyset(&act.sa_mask);

    for (int i = 0; i < kNumHandledSignals; ++i)
        sigaddset(&act.sa_mask, kExceptionSignals[i]);

    act.sa_sigaction = SignalHandler;
    act.sa_flags = SA_ONSTACK | SA_SIGINFO;

    for (int i = 0; i < kNumHandledSignals; ++i)
        sigaction(kExceptionSignals[i], &act, NULL);
}

bool linuxDumpCallback(const google_breakpad::MinidumpDescriptor& descriptor, void* context, bool succeeded)
{
    static auto logger = g_ifaceService.FetchInterface<ILogger>(LOGGER_INTERFACE_VERSION);
    ConsoleLogger_FlushForCrash();

    auto tracerLevel = g_ifaceService.FetchInterface<ICrashReporter>(CRASHREPORTER_INTERFACE_VERSION)->GetDotnetCrashTracerLevel();
    if (tracerLevel > 0)
    {
        std::string tracerPath = g_dumpPath + "/managedtrace.txt";
        logger->Warning("Crash Reporter", fmt::format("Dumping managed trace to: {}\n", tracerPath));
        TracerDump(g_SwiftlyCore.GetCorePath(), tracerPath.c_str());
    }

    std::string mdmpPath = descriptor.path();

    if (!succeeded) {
        logger->Error("Crash Reporter", fmt::format("Failed to write minidump to '{}'\n", mdmpPath));
        return succeeded;
    }

    g_dumpWritten = true;
    logger->Info("Crash Reporter", fmt::format("Minidump written to '{}'\n", mdmpPath));

    google_breakpad::SimpleSymbolSupplier symbolSupplier("");
    google_breakpad::BasicSourceLineResolver resolver;
    google_breakpad::MinidumpProcessor minidump_processor(&symbolSupplier, &resolver);

    google_breakpad::MinidumpThreadList::set_max_threads(std::numeric_limits<uint32_t>::max());
    google_breakpad::MinidumpMemoryList::set_max_regions(std::numeric_limits<uint32_t>::max());

    google_breakpad::Minidump mdmp(mdmpPath);
    if (!mdmp.Read()) {
        logger->Error("Crash Reporter", fmt::format("Failed to read minidump from '{}'\n", mdmpPath));
        return succeeded;
    }
    else {
        google_breakpad::ProcessState processState;
        if (minidump_processor.Process(&mdmp, &processState) != google_breakpad::PROCESS_OK)
        {
            logger->Error("Crash Reporter", fmt::format("MinidumpProcessor::Process failed\n", mdmpPath));
        }
        else
        {
            auto crashInfoJson = FormatProcessState(processState, &resolver);
            Files::Write(g_relativeDumpPath + "/crashinfo.json", crashInfoJson, false);
        }
    }

    return succeeded;
}

#endif