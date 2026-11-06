using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAdvancedTerminal.Utility
{
    public static class EditorMainThreadDispatcher
    {
        private static readonly Queue<Action> executionQueue = new Queue<Action>();
        private static readonly object lockObj = new object();

        static EditorMainThreadDispatcher()
        {
            EditorApplication.update += Update;
        }

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (lockObj)
            {
                executionQueue.Enqueue(action);
            }
        }

        private static void Update()
        {
            lock (lockObj)
            {
                while (executionQueue.Count > 0)
                {
                    executionQueue.Dequeue().Invoke();
                }
            }
        }
    }
}