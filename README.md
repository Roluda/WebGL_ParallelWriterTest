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
