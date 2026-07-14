#include "pch.h"
#include "Shared/Emulator.h"
#include "Shared/DebuggerRequest.h"
#include <type_traits>

static_assert(!std::is_copy_constructible_v<DebuggerRequest>);
static_assert(!std::is_copy_assignable_v<DebuggerRequest>);
static_assert(!std::is_move_constructible_v<DebuggerRequest>);
static_assert(!std::is_move_assignable_v<DebuggerRequest>);

DebuggerRequest::DebuggerRequest(Emulator* emu)
	: _ownerThreadId(std::this_thread::get_id())
{
	if(emu) {
		_emu = emu;
		_debugger = _emu->_debugger.lock();
		_emu->RegisterDebuggerRequest(_ownerThreadId);
	}
}

DebuggerRequest::~DebuggerRequest()
{
	if(_emu) {
		_emu->UnregisterDebuggerRequest(_ownerThreadId);
	}
}
