#pragma once
#include "pch.h"

class Debugger;
class Emulator;

class DebuggerRequest
{
private:
	shared_ptr<Debugger> _debugger;
	Emulator* _emu = nullptr;
	thread::id _ownerThreadId;

public:
	DebuggerRequest(Emulator* emu);
	~DebuggerRequest();
	DebuggerRequest(const DebuggerRequest&) = delete;
	DebuggerRequest& operator=(const DebuggerRequest&) = delete;
	DebuggerRequest(DebuggerRequest&&) = delete;
	DebuggerRequest& operator=(DebuggerRequest&&) = delete;

	Debugger* GetDebugger() { return _debugger.get(); }
};
