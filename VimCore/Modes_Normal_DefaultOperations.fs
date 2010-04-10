﻿#light

namespace Vim.Modes.Normal
open Vim
open Vim.Modes
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations

type internal DefaultOperations
    (
    _textView : ITextView,
    _operations : IEditorOperations,
    _host : IVimHost,
    _statusUtil : IStatusUtil,
    _settings : IVimLocalSettings,
    _normalWordNav : ITextStructureNavigator,
    _jumpList : IJumpList,
    _incrementalSearch : IIncrementalSearch) =

    inherit CommonOperations(_textView, _operations, _host, _jumpList, _settings)

    let _search = _incrementalSearch.SearchService

    member private x.CommonImpl = x :> ICommonOperations

    member private x.JumpCore count moveJump =
        let rec inner count = 
            if count >= 1 && moveJump() then inner (count-1)
            elif count = 0 then true
            else false
        if not (inner count) then _host.Beep()
        else
            match _jumpList.Current with
            | None -> _host.Beep()
            | Some(point) -> 
                let ret = x.CommonImpl.NavigateToPoint (VirtualSnapshotPoint(point))
                if not ret then _host.Beep()

    member private x.MoveToNextWordCore kind count isWholeWord = 
        let point = ViewUtil.GetCaretPoint _textView
        match TssUtil.FindCurrentFullWordSpan point WordKind.NormalWord with
        | None -> _statusUtil.OnError Resources.NormalMode_NoWordUnderCursor
        | Some(span) ->

            // Build up the SearchData structure
            let word = span.GetText()
            let text = if isWholeWord then WholeWord(word) else StraightText(word)
            let data = {Text=text; Kind = kind; Options = SearchOptions.AllowIgnoreCase }

            // When forward the search will be starting on the current word so it will 
            // always match.  Without modification a count of 1 would simply find the word 
            // under the cursor.  Increment the count by 1 here so that it will find
            // the current word as the 0th match (so to speak)
            let count = if SearchKindUtil.IsForward kind then count + 1 else count 

            match _search.FindNextMultiple data point _normalWordNav count with
            | Some(span) -> ViewUtil.MoveCaretToPoint _textView span.Start |> ignore
            | None -> ()

            _search.LastSearch <- data

    member x.GoToLineCore line =
        let snapshot = _textView.TextSnapshot
        let lastLineNumber = snapshot.LineCount - 1
        let line = min line lastLineNumber
        let textLine = snapshot.GetLineFromLineNumber(line)
        if _settings.GlobalSettings.StartOfLine then 
            _textView.Caret.MoveTo( textLine.Start ) |> ignore
            _operations.MoveToStartOfLineAfterWhiteSpace(false)
        else 
            let point = ViewUtil.GetCaretPoint _textView
            let _,column = SnapshotPointUtil.GetLineColumn point
            let column = min column textLine.Length
            let point = textLine.Start.Add(column)
            _textView.Caret.MoveTo (point) |> ignore

    interface IOperations with 
        
        /// Paste the given text after the cursor
        member x.PasteAfterCursor text count opKind moveCursor = 
            let text = StringUtil.repeat text count 
            let caret = ViewUtil.GetCaretPoint _textView
            let span = x.CommonImpl.PasteAfter caret text opKind
            if moveCursor then
                ViewUtil.MoveCaretToPoint _textView span.End |> ignore
            else if opKind = OperationKind.LineWise then
                // For a LineWise paste we want to place the cursor at the start
                // of the next line
                let caretLineNumber = caret.GetContainingLine().LineNumber
                let nextLine = _textView.TextSnapshot.GetLineFromLineNumber(caretLineNumber + 1)
                let point = TssUtil.FindFirstNonWhitespaceCharacter nextLine
                ViewUtil.MoveCaretToPoint _textView point |> ignore
 
        /// Paste the text before the cursor
        member x.PasteBeforeCursor text count opKind moveCursor = 
            let text = StringUtil.repeat text count 
            let caret = ViewUtil.GetCaretPoint _textView
            let span = x.CommonImpl.PasteBefore caret text opKind
            if moveCursor then
                ViewUtil.MoveCaretToPoint _textView span.End |> ignore
            else if opKind = OperationKind.LineWise then
                // For a LineWise paste we want to place the cursor at the start of this line. caret is a a snapshot
                // point from the old snapshot, so we need to find the same line in the new snapshot
                let line = _textView.TextSnapshot.GetLineFromLineNumber(caret.GetContainingLine().LineNumber)
                let point = TssUtil.FindFirstNonWhitespaceCharacter line
                ViewUtil.MoveCaretToPoint _textView point |> ignore

        member x.InsertLineBelow () =
            let point = ViewUtil.GetCaretPoint _textView
            let line = point.GetContainingLine()
            let buffer = line.Snapshot.TextBuffer
            buffer.Replace(new Span(line.End.Position,0), System.Environment.NewLine) |> ignore
            let newLine = buffer.CurrentSnapshot.GetLineFromLineNumber(line.LineNumber+1)
        
            // Move the caret to the same indent position as the previous line
            let indent = TssUtil.FindIndentPosition(line)
            let point = new VirtualSnapshotPoint(newLine, indent)
            ViewUtil.MoveCaretToVirtualPoint _textView point |> ignore
            newLine
    
        member x.InsertLineAbove () = 
            let point = ViewUtil.GetCaretPoint _textView
            let line = point.GetContainingLine()
            let buffer = line.Snapshot.TextBuffer
            buffer.Replace(new Span(line.Start.Position,0), System.Environment.NewLine) |> ignore
            let line = buffer.CurrentSnapshot.GetLineFromLineNumber(line.LineNumber)
            ViewUtil.MoveCaretToPoint _textView line.Start |> ignore
            line
                
        /// Implement the r command in normal mode.  
        member x.ReplaceChar (ki:KeyInput) count = 
            let point = ViewUtil.GetCaretPoint _textView

            // Make sure the replace string is valid
            if (point.Position + count) > point.GetContainingLine().End.Position then
                false
            else
                let replaceText = 
                    if ki.IsNewLine then System.Environment.NewLine
                    else new System.String(ki.Char, count)
                let span = new Span(point.Position, count)
                let tss = _textView.TextBuffer.Replace(span, replaceText) 

                // Reset the caret to the point before the edit
                let point = new SnapshotPoint(tss,point.Position)
                _textView.Caret.MoveTo(point) |> ignore
                true
    
        /// Yank lines from the buffer.  Implements the Y command
        member x.YankLines count reg =
            let point = ViewUtil.GetCaretPoint _textView
            let point = point.GetContainingLine().Start
            let span = SnapshotPointUtil.GetLineRangeSpanIncludingLineBreak point count
            x.CommonImpl.Yank span MotionKind.Inclusive OperationKind.LineWise reg |> ignore

    
        /// Implement the normal mode x command
        member x.DeleteCharacterAtCursor count reg =
            let point = ViewUtil.GetCaretPoint _textView
            let line = point.GetContainingLine()
            let count = min (count) (line.End.Position-point.Position)
            let span = new SnapshotSpan(point, count)
            x.CommonImpl.DeleteSpan span MotionKind.Exclusive OperationKind.CharacterWise reg |> ignore
    
        /// Implement the normal mode X command
        member x.DeleteCharacterBeforeCursor count reg = 
            let point = ViewUtil.GetCaretPoint _textView
            let range = TssUtil.GetReverseCharacterSpan point count
            x.CommonImpl.DeleteSpan range MotionKind.Exclusive OperationKind.CharacterWise reg |> ignore
    
        member x.JoinAtCaret count =     
            let start = ViewUtil.GetCaretPoint _textView
            let kind = Vim.Modes.JoinKind.RemoveEmptySpaces
            let res = x.CommonImpl.Join start kind count
            if not res then
                _host.Beep()

        member x.GoToDefinitionWrapper () =
            match x.CommonImpl.GoToDefinition() with
            | Vim.Modes.Succeeded -> ()
            | Vim.Modes.Failed(msg) -> _statusUtil.OnError msg

        member x.MoveToNextOccuranceOfWordAtCursor kind count =  x.MoveToNextWordCore kind count true
        member x.MoveToNextOccuranceOfPartialWordAtCursor kind count = x.MoveToNextWordCore kind count false
        member x.JumpNext count = x.JumpCore count (fun () -> _jumpList.MoveNext())
        member x.JumpPrevious count = x.JumpCore count (fun() -> _jumpList.MovePrevious())
        member x.FindNextMatch count =
            let last = _incrementalSearch.SearchService.LastSearch
            if StringUtil.isNullOrEmpty last.Text.RawText then 
                _statusUtil.OnError Resources.NormalMode_NoPreviousSearch
            elif not (_incrementalSearch.FindNextMatch count) then
                _statusUtil.OnError (Resources.NormalMode_PatternNotFound last.Text.RawText)
    
        member x.GoToLineOrFirst count =
            let line =
                match count with
                | None -> 0
                | Some(c) -> c
            x.GoToLineCore line

        member x.GoToLineOrLast count =
            let snapshot = _textView.TextSnapshot
            let lastLineNumber = snapshot.LineCount - 1
            let line = 
                match count with
                | None -> lastLineNumber
                // Surprisingly 0 goes to the last line nmuber in gVim
                | Some(c) when c = 0 -> lastLineNumber
                | Some(c) -> c
            x.GoToLineCore line

        member x.ChangeLetterCaseAtCursor count = 
            let point = ViewUtil.GetCaretPoint _textView
            let line = SnapshotPointUtil.GetContainingLine point
            let count = min count (line.End.Position - point.Position)
            let span = SnapshotSpan(point, count)
            x.CommonImpl.ChangeLetterCase span

            if line.Length > 0 then
                
                // Because we aren't changing the length of the buffer it's OK 
                // to calculate with respect to the points before the edit
                let pos = point.Position + count
                let pos = min pos (line.End.Position-1)
                ViewUtil.MoveCaretToPosition _textView pos |> ignore
            


