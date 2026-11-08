using System;
using System.Collections.Generic;

namespace Frosty.Core.Managers
{
    public interface IUndoUnit
    {
        string Text { get; }
        void Do();
        void Undo();
    }

    /// <summary>
    /// An undo unit that can be applied again after Undo (UndoManager.Redo). Do() is only called
    /// once, when the change is committed, and often just marks the asset as modified.
    /// </summary>
    public interface IRedoableUndoUnit : IUndoUnit
    {
        void Redo();
    }

    /// <summary>Redo of a generic unit runs its do-action again (the same thing commit ran).</summary>
    public class GenericUndoUnit : IRedoableUndoUnit
    {
        public string Text { get; private set; }

        private Action<object> doAction;
        private Action<object> undoAction;

        public GenericUndoUnit(string text, Action<object> inDoAction, Action<object> inUndoAction)
        {
            Text = text;
            doAction = inDoAction;
            undoAction = inUndoAction;
        }

        public void Do()
        {
            doAction.Invoke(null);
        }

        public void Undo()
        {
            undoAction.Invoke(null);
        }

        public void Redo()
        {
            doAction.Invoke(null);
        }
    }

    public class UndoContainer : IRedoableUndoUnit
    {
        public string Text { get; private set; }

        public bool HasItems => children.Count > 0;
        private List<IUndoUnit> children = new List<IUndoUnit>();

        public UndoContainer(string text)
        {
            Text = text;
        }

        public void Add(IUndoUnit undoUnit)
        {
            children.Add(undoUnit);
        }

        public void Do()
        {
            foreach (var undoUnit in children)
            {
                undoUnit.Do();
            }
        }

        public void Undo()
        {
            for (int i = children.Count - 1; i >= 0; i--)
            {
                children[i].Undo();
            }
        }

        public void Redo()
        {
            foreach (var undoUnit in children)
            {
                if (undoUnit is IRedoableUndoUnit redoable)
                    redoable.Redo();
                else
                    undoUnit.Do();
            }
        }
    }

    public class UndoManager
    {
        #region -- Singleton --

        public static UndoManager Instance { get; private set; } = new UndoManager();
        private UndoManager() { }

        #endregion

        public IUndoUnit PendingUndoUnit
        {
            get => pendingUndoUnit;
            set
            {
                if (pendingUndoUnit != value)
                {
                    pendingUndoUnit = value;
                }
            }
        }
        public bool IsUndoing
        {
            get => isUndoing;
            set
            {
                if (isUndoing != value)
                {
                    isUndoing = value;
                }
            }
        }

        private IUndoUnit pendingUndoUnit;
        private Stack<IUndoUnit> undoStack = new Stack<IUndoUnit>();
        private Stack<IUndoUnit> redoStack = new Stack<IUndoUnit>();
        private bool isUndoing;

        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;

        public void CommitUndo(IUndoUnit undoUnit)
        {
            // callers commit PendingUndoUnit, which is null when nothing started one (e.g. a typed
            // value without a drag); pushing null broke Undo
            if (undoUnit == null)
                return;

            undoStack.Push(undoUnit);
            redoStack.Clear();

            undoUnit.Do();
            if (undoUnit == pendingUndoUnit)
            {
                pendingUndoUnit = null;
            }
        }

        public void Undo()
        {
            if (undoStack.Count > 0)
            {
                isUndoing = true;

                IUndoUnit undoUnit = undoStack.Pop();
                redoStack.Push(undoUnit);

                App.NotificationManager.Show($"Undo: {undoUnit.Text}");

                undoUnit.Undo();
                isUndoing = false;
            }
        }

        public void Redo()
        {
            if (redoStack.Count == 0)
                return;

            IUndoUnit undoUnit = redoStack.Pop();
            if (!(undoUnit is IRedoableUndoUnit redoable))
            {
                // skip it rather than throwing the rest of the redo history away
                App.NotificationManager.Show($"Cannot redo (skipped): {undoUnit.Text}");
                return;
            }

            isUndoing = true;
            try
            {
                redoable.Redo();
            }
            catch (Exception ex)
            {
                // drop this step instead of leaving it on top of the redo stack, where every
                // further Redo would hit it again; the rest of the history stays usable
                App.Logger.LogError($"Redo of '{undoUnit.Text}' failed and was skipped: {ex.Message}");
                App.NotificationManager.Show($"Redo failed (skipped): {undoUnit.Text}");
                return;
            }
            finally
            {
                isUndoing = false;
            }
            undoStack.Push(undoUnit);
            App.NotificationManager.Show($"Redo: {undoUnit.Text}");
        }

        public void Clear()
        {
            undoStack.Clear();
            redoStack.Clear();
            pendingUndoUnit = null;
            isUndoing = false;
        }

        public static UndoManager Create()
        {
            return new UndoManager();
        }

        public static void SetCurrent(UndoManager manager)
        {
            Instance = manager;
        }

        public static void ClearCurrent()
        {
            Instance = null;
        }
    }
}