using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace UMA.Terraforma
{
    public class NativeListParallelWriterTest : MonoBehaviour
    {
        private const int Count = 100_000;
        private const int Batch = 64;

        private Result _intResult;
        private Result _int3Result;
        private bool _hasRun;

        private struct Result
        {
            public string Label;
            public int Workers;
            public int Expected;
            public int Length;
            public int Missing;
            public int Duplicates;
            public int OutOfRange;
            public int Torn;
            public bool Broken;
        }

        private void Start()
        {
            RunTests();
        }

        private void RunTests()
        {
            _intResult = RunInt();
            _int3Result = RunInt3();
            _hasRun = true;
        }

        private static Result RunInt()
        {
            var list = new NativeList<int>(Count, Allocator.Persistent);
            new FillIntJob { Writer = list.AsParallelWriter() }.Schedule(Count, Batch).Complete();

            int writtenLength = list.Length;
            var seen = new NativeArray<int>(Count, Allocator.Temp);
            int outOfRange = 0;
            for (int i = 0; i < writtenLength; i++)
            {
                int v = list[i];
                if ((uint)v < (uint)Count) seen[v]++;
                else outOfRange++;
            }

            int missing = 0;
            int duplicates = 0;
            for (int i = 0; i < Count; i++)
            {
                int c = seen[i];
                if (c == 0) missing++;
                else duplicates += c - 1;
            }

            seen.Dispose();
            list.Dispose();

            return BuildResult("int", writtenLength, missing, duplicates, outOfRange, 0);
        }

        private static Result RunInt3()
        {
            var list = new NativeList<int3>(Count, Allocator.Persistent);
            new FillInt3Job { Writer = list.AsParallelWriter() }.Schedule(Count, Batch).Complete();

            int writtenLength = list.Length;
            var seen = new NativeArray<int>(Count, Allocator.Temp);
            int outOfRange = 0;
            int torn = 0;
            for (int i = 0; i < writtenLength; i++)
            {
                int3 v = list[i];
                if ((uint)v.x < (uint)Count && v.y == v.x * 2 && v.z == v.x * 3)
                    seen[v.x]++;
                else if ((uint)v.x < (uint)Count)
                    torn++;
                else
                    outOfRange++;
            }

            int missing = 0;
            int duplicates = 0;
            for (int i = 0; i < Count; i++)
            {
                int c = seen[i];
                if (c == 0) missing++;
                else duplicates += c - 1;
            }

            seen.Dispose();
            list.Dispose();

            return BuildResult("int3", writtenLength, missing, duplicates, outOfRange, torn);
        }

        private static Result BuildResult(string label, int length, int missing, int duplicates, int outOfRange, int torn)
        {
            return new Result
            {
                Label = label,
                Workers = JobsUtility.JobWorkerCount,
                Expected = Count,
                Length = length,
                Missing = missing,
                Duplicates = duplicates,
                OutOfRange = outOfRange,
                Torn = torn,
                Broken = length != Count || missing > 0 || duplicates > 0 || outOfRange > 0 || torn > 0,
            };
        }

        private void OnGUI()
        {
            const float pad = 10f;
            const float width = 560f;
            const float height = 360f;

            GUI.skin.label.fontSize = 16;
            GUI.skin.button.fontSize = 16;
            GUI.skin.box.fontSize = 16;

            GUILayout.BeginArea(new Rect(pad, pad, width, height), GUI.skin.box);

            GUILayout.Label("NativeList.ParallelWriter Test");

            if (!_hasRun)
            {
                GUILayout.Label("Running...");
            }
            else
            {
                DrawResult(_intResult);
                GUILayout.Space(8);
                DrawResult(_int3Result);
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Re-run", GUILayout.Height(28)))
            {
                RunTests();
            }

            GUILayout.EndArea();
        }

        private static readonly StringBuilder _sb = new StringBuilder();

        private static void DrawResult(Result r)
        {
            _sb.Clear();
            _sb.Append('[').Append(r.Label).Append("] workers=").Append(r.Workers).Append('\n');
            _sb.Append("expected=").Append(r.Expected).Append("  actual length=").Append(r.Length).Append('\n');
            _sb.Append("missing=").Append(r.Missing)
                .Append("  duplicates=").Append(r.Duplicates)
                .Append("  outOfRange=").Append(r.OutOfRange);
            if (r.Label == "int3") _sb.Append("  torn=").Append(r.Torn);

            var prev = GUI.color;
            GUI.color = r.Broken ? new Color(1f, 0.4f, 0.4f) : new Color(0.5f, 1f, 0.5f);
            GUILayout.Label((r.Broken ? "BROKEN " : "OK ") + _sb.ToString());
            GUI.color = prev;
        }

        [BurstCompile]
        private struct FillIntJob : IJobParallelFor
        {
            public NativeList<int>.ParallelWriter Writer;

            public void Execute(int index)
            {
                Writer.AddNoResize(index);
            }
        }

        [BurstCompile]
        private struct FillInt3Job : IJobParallelFor
        {
            public NativeList<int3>.ParallelWriter Writer;

            public void Execute(int index)
            {
                Writer.AddNoResize(new int3(index, index * 2, index * 3));
            }
        }
    }
}
