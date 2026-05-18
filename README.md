Reproduction: 
1. Selcet Web as Build Target
2. Build and Run

This requires the `Native Multithreading` toggled on in the Build Settings (Web - Release Settings)

Expected result (Editor)

<img width="754" height="625" alt="Test_Editor" src="https://github.com/user-attachments/assets/5efcc947-9214-4915-b6c3-e280b91e9224" />


Actual Result (Build)

<img width="1119" height="726" alt="Test_Build" src="https://github.com/user-attachments/assets/4c7175af-9614-4868-b2d0-6b16a87f8ef5" />

Reasoning:

The burst compiler compiled the .wasm assembly code with non interlocked instructions, which don't properly increment the counter of the list, which causes writes to the same address.

`Writer.AddNoResize(value)` should boil down to:

```csharp
int idx = Interlocked.Increment(ref m_Length) - 1;   // reserve a slot
m_Buffer[idx] = value;                               // write into it
```

The "reserve a slot" step has to be atomic, otherwise two workers grab the same index and stomp on each other. Here's what Burst actually emits for one of those slot reservations:

```wasm
; local 9 = &m_Length

local.get 9
i32.load  0          ; read m_Length
local.tee 12         ; idx = old length
i32.const 1
i32.add
i32.store 0          ; write back length + 1

; ...then store the value at m_Buffer[idx]
```

That's a plain load / add / store on a memory location that other threads are also poking at. Two workers can both read length `42`, both think they got slot `42`, both write there, and length only goes up by one. Hence the missing entries, duplicates, and short `Length` we see in the build.

The whole five-instruction sequence above should just be:

```wasm
local.get 9
i32.const 1
i32.atomic.rmw.add 0   ; atomically: *p += 1, returns old value
local.tee 12           ; idx = old length
```

One atomic instruction, race gone, and the returned old value is exactly the slot index we wanted anyway.

This same `load / tee / const 1 / add / store` pattern shows up **five times** in the function — once in the scalar tail loop and four more times in the 4x-unrolled main loop. Every single one needs the same swap to `i32.atomic.rmw.add`.
