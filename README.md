Reproduction: 
1. Selcet Web as Build Target
2. Build and Run

Expected result (Editor)
<img width="754" height="625" alt="Test_Editor" src="https://github.com/user-attachments/assets/5efcc947-9214-4915-b6c3-e280b91e9224" />


Actual Result (Build)
<img width="1119" height="726" alt="Test_Build" src="https://github.com/user-attachments/assets/4c7175af-9614-4868-b2d0-6b16a87f8ef5" />

Reasoning:

The burst compiler compiled the .wasm assembly code with non interlocked instructions, which don't properly increment the counter of the list, which causes writes to the same address.

This requires the `Native Multithreading` toggled on in the Build Settings (Web - Release Settings)

---

## Annotated WASM dump

Below is the Burst-emitted WASM for the `FillIntJob.Execute` worker entry point (the `IJobParallelFor` range loop). The job calls `Writer.AddNoResize(index)` which expands to:

```csharp
int idx = Interlocked.Increment(ref m_Length) - 1;
m_Buffer[idx] = value;
```

The problem: every site that should be a single atomic RMW on `m_Length` is emitted as a non-atomic `load` / `add` / `store` triple. Two parallel workers can read the same length, both write at the same slot, and the length advances by 1 instead of 2 — producing the `missing`, `duplicates`, and short `Length` results seen in the build.

The five racy sites are marked **`RACE`** below. Each one should collapse to a single `i32.atomic.rmw.add` with `i32.const 1`.

```wasm
; ============================================================
; FillIntJob.Execute worker — Burst-compiled IJobParallelFor body
; Args: (i32 jobData, i32 ?, i32 ?, i32 beginIndex, i32 endIndex)
; ============================================================
.functype 06a383d13b415260a915909ef72a6136 (i32, i32, i32, i32, i32) -> ()
.local    i32, i32, i32, i32, i32, i32, i32, i32

; ---- Stack frame setup: reserve 16 bytes for two locals
;      [sp+8]  = current index   (work-stealing 'begin')
;      [sp+12] = end index       (work-stealing 'end')
global.get __stack_pointer
i32.const  16
i32.sub 
local.tee  5                          ; local 5 = frame pointer
global.set __stack_pointer
local.get  5
i32.const  0
i32.store  12                         ; [sp+12] = 0
local.get  5
i32.const  0
i32.store  8                          ; [sp+8]  = 0

; ---- Ask JobsUtility for the next work-stealing range
;      GetWorkStealingRange(jobIndex, workerIndex, &begin, &end)
;      Returns 0 when no more work is available.
block
  local.get 3                         ; jobIndex
  local.get 4                         ; workerIndex
  local.get 5
  i32.const 8
  i32.add                             ; &begin = sp+8
  local.get 5
  i32.const 12
  i32.add                             ; &end   = sp+12
  global.get "Unity.Jobs.LowLevel.Unsafe.JobsUtility::GetWorkStealingRange_Ptr"@GOT
  i32.load  0
  local.tee 6                         ; cache function pointer
  call_indirect __indirect_function_table, (i32,i32,i32,i32) -> (i32)
  i32.const 255
  i32.and 
  i32.eqz
  br_if 0                             ; no more work -> exit outer block

  ; ---- Outer per-range loop
  .LBB0_2:
  loop
    block
      local.get 5
      i32.load  8
      local.tee 7                     ; idx = begin
      local.get 5
      i32.load  12
      local.tee 8                     ; end
      i32.ge_s
      br_if 0                         ; if (idx >= end) skip body

      ; ---- Load &m_Length (NativeList header field) into local 9
      local.get 0
      i32.load  0:p2align=0
      local.set 9                     ; local 9 = &m_Length

      ; ---- Compute (end - begin) % 4 to peel off the unrolled tail
      block
        block
          local.get 8
          local.get 7
          i32.sub
          i32.const 3
          i32.and
          local.tee 10                ; tail count = (end-begin) & 3
          br_if 0                     ; if tail != 0  -> run scalar loop
          local.get 7
          local.set 11                ; otherwise idx -> directly to unrolled
          br 1
        .LBB0_5:
        end_block
        local.get 7
        local.set 11                  ; local 11 = current write value (= idx)

        ; ============================================================
        ; .LBB0_6 — SCALAR TAIL LOOP (runs 'tail count' iterations)
        ; One AddNoResize per pass.
        ; ============================================================
        .LBB0_6:
        loop
          local.get 9
          i32.const 4
          i32.add                     ; advance &m_Length pointer
          local.tee 9

          ; >>> RACE #1: non-atomic Interlocked.Increment(&m_Length) <<<
          ;   Should be:
          ;       local.get 9
          ;       i32.const 1
          ;       i32.atomic.rmw.add 0
          ;       local.tee 12        ; returned old length = reserved idx
          local.get 9
          i32.load  0                 ; <-- racy load of m_Length
          local.tee 12                ;     save old length
          i32.const 1
          i32.add
          i32.store 0                 ; <-- racy store of m_Length + 1

          ; ---- Store value at m_Buffer[idx]
          local.get 0
          i32.load  0:p2align=0
          local.tee 9
          i32.load  0:p2align=0       ; m_Buffer base
          local.get 12
          i32.const 2
          i32.shl                     ; idx * sizeof(int)
          i32.add                     ; &m_Buffer[idx]
          local.get 11                ; value to write
          i32.store 0:p2align=0

          local.get 11
          i32.const 1
          i32.add
          local.set 11                ; value++
          local.get 10
          i32.const -1
          i32.add
          local.tee 10
          br_if 0                     ; while (--tail)
        .LBB0_7:
        end_loop
      end_block

      ; ---- If range is so small the unrolled body would overrun, skip it
      local.get 7
      local.get 8
      i32.sub
      i32.const -4
      i32.gt_u
      br_if 0

      ; ============================================================
      ; .LBB0_8 — 4x UNROLLED MAIN LOOP
      ; Four AddNoResize calls per iteration. All four contain the
      ; same non-atomic Interlocked.Increment pattern.
      ; ============================================================
      .LBB0_8:
      loop
        ; ---- Unroll #1
        local.get 9
        i32.const 4
        i32.add
        local.tee 9
        ; >>> RACE #2 <<<
        local.get 9
        i32.load  0
        local.tee 9                   ; old length
        i32.const 1
        i32.add
        i32.store 0
        local.get 0
        i32.load  0:p2align=0
        local.tee 10
        i32.load  0:p2align=0         ; m_Buffer
        local.get 9
        i32.const 2
        i32.shl
        i32.add
        local.get 11                  ; value = base + 0
        i32.store 0:p2align=0

        ; ---- Unroll #2
        local.get 10
        i32.const 4
        i32.add
        local.tee 9
        ; >>> RACE #3 <<<
        local.get 9
        i32.load  0
        local.tee 9
        i32.const 1
        i32.add
        i32.store 0
        local.get 0
        i32.load  0:p2align=0
        local.tee 10
        i32.load  0:p2align=0
        local.get 9
        i32.const 2
        i32.shl
        i32.add
        local.get 11
        i32.const 1
        i32.add                       ; value = base + 1
        i32.store 0:p2align=0

        ; ---- Unroll #3
        local.get 10
        i32.const 4
        i32.add
        local.tee 9
        ; >>> RACE #4 <<<
        local.get 9
        i32.load  0
        local.tee 9
        i32.const 1
        i32.add
        i32.store 0
        local.get 0
        i32.load  0:p2align=0
        local.tee 10
        i32.load  0:p2align=0
        local.get 9
        i32.const 2
        i32.shl
        i32.add
        local.get 11
        i32.const 2
        i32.add                       ; value = base + 2
        i32.store 0:p2align=0

        ; ---- Unroll #4
        local.get 10
        i32.const 4
        i32.add
        local.tee 9
        ; >>> RACE #5 <<<
        local.get 9
        i32.load  0
        local.tee 10
        i32.const 1
        i32.add
        i32.store 0
        local.get 0
        i32.load  0:p2align=0
        local.tee 9
        i32.load  0:p2align=0
        local.get 10
        i32.const 2
        i32.shl
        i32.add
        local.get 11
        i32.const 3
        i32.add                       ; value = base + 3
        i32.store 0:p2align=0

        local.get 11
        i32.const 4
        i32.add                       ; value += 4 for next iteration
        local.tee 11
        local.get 8
        i32.ne
        br_if 0                       ; while (value != end)
      .LBB0_9:
      end_loop
    end_block

    ; ---- Ask for another work-stealing range; exit when none remain
    local.get 3
    local.get 4
    local.get 5
    i32.const 8
    i32.add
    local.get 5
    i32.const 12
    i32.add
    local.get 6
    call_indirect __indirect_function_table, (i32,i32,i32,i32) -> (i32)
    i32.const 255
    i32.and
    br_if 0                           ; if returned non-zero, loop
  .LBB0_10:
  end_loop
end_block

; ---- Tear down stack frame
local.get 5
i32.const 16
i32.add
global.set __stack_pointer
end_function
```

### Summary of the five racy sites

| # | Location          | Bad sequence (on `&m_Length`)                                  | Should be                       |
| - | ----------------- | -------------------------------------------------------------- | ------------------------------- |
| 1 | `.LBB0_6` (tail)  | `i32.load 0` / `local.tee 12` / `i32.const 1` / `i32.add` / `i32.store 0` | `i32.const 1` / `i32.atomic.rmw.add 0` |
| 2 | `.LBB0_8` unroll 1 | same pattern                                                  | same fix                        |
| 3 | `.LBB0_8` unroll 2 | same pattern                                                  | same fix                        |
| 4 | `.LBB0_8` unroll 3 | same pattern                                                  | same fix                        |
| 5 | `.LBB0_8` unroll 4 | same pattern                                                  | same fix                        |

Each `i32.atomic.rmw.add` would return the previous value of `m_Length`, which is exactly the slot index the caller needs — so the load/tee/const/add/store quintuple collapses to a single instruction that also happens to be race-free.

The element store that follows each race (`i32.store 0:p2align=0` into `m_Buffer[idx]`) is **not** itself racy once each worker holds a uniquely reserved index — only the length increment needs to become atomic.
