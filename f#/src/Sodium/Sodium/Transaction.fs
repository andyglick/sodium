﻿namespace Sodium

open System
open System.Collections.Generic
open System.Linq
open Priority_Queue

type Transaction() =
    static let transactionLock = obj()
    static let mutable currentTransaction = Option<Transaction>.None
    static let onStartHooks = List<unit -> unit>()
    static let mutable runningOnStartHooks = false

    let entries = HashSet<Entry>()
    let lastQueue = List<unit -> unit>()
    let postQueueFirst = List<unit -> unit>()
    let postQueue = Dictionary<int, Transaction -> unit>()
    let prioritizedQueue = SimplePriorityQueue<Entry>()

    let mutable toRegen = false

    let checkRegen () =
        if toRegen then
            toRegen <- false
            prioritizedQueue.Clear()
            for e in entries do
                prioritizedQueue.Enqueue(e, float e.Rank.Rank)

    static let startIfNecessary () =
        match currentTransaction with
        | None ->
            if not runningOnStartHooks then
                runningOnStartHooks <- true
                try
                    for action in onStartHooks do action ()
                finally
                    runningOnStartHooks <- false
            else ()

            let t = Transaction()
            currentTransaction <- Option.Some t
            t
        | Some t -> t

    static member val internal InCallback = 0 with get,set

    static member internal GetCurrentTransaction () =
        lock transactionLock (fun () -> currentTransaction)

    static member internal HasCurrentTransaction () =
        match Transaction.GetCurrentTransaction () with
        | None -> false
        | Some _ -> true

    static member Run f = Transaction.Apply (fun t -> f())

    static member internal Apply f =
        lock transactionLock (fun () ->
            let transWas = currentTransaction
            try
                let t = startIfNecessary ()
                f (t)
            finally
                try
                    match transWas with
                    | None ->
                        match currentTransaction with
                        | None -> ()
                        | Some t -> t.Close()
                    | Some t -> ()
                finally
                    currentTransaction <- transWas)

        static member OnStart action = lock transactionLock (fun () -> onStartHooks.Add(action))

        member internal __.Prioritized rank action =
            let e = Entry(rank, action)
            prioritizedQueue.Enqueue(e, float rank.Rank)
            entries.Add(e) |> ignore

        member internal __.Last action = lastQueue.Add(action)

        member internal __.Post(index, action) =
            let foundExisting, existing = postQueue.TryGetValue(index)
            let ``new`` =
                if foundExisting then
                    (fun (trans : Transaction) ->
                        existing trans
                        action trans)
                else action
            postQueue.[index] <- ``new``

        member internal __.Post action =
            postQueueFirst.Add(action)

        static member Post action =
            Transaction.Apply (fun trans -> Transaction.Post action)

        member internal __.SetNeedsRegenerating () = toRegen <- true

        member internal this.Close() =
            let rec dequeueLoop () =
                checkRegen ()
                if prioritizedQueue.Any() then
                    let e = prioritizedQueue.Dequeue()
                    entries.Remove(e) |> ignore
                    e.Action this
                    dequeueLoop ()

            dequeueLoop ()

            let parent = currentTransaction
            try
                currentTransaction <- Option.None
                for action in postQueueFirst do action ()
            finally
                currentTransaction <- parent

            postQueueFirst.Clear()

            for KeyValue(index, action) in postQueue do
                let parent = currentTransaction
                try
                    let transaction = Transaction()
                    currentTransaction <- Option.Some transaction
                    action transaction
                finally
                    currentTransaction <- parent

            postQueue.Clear()

and internal Entry(rank : Node, action : Transaction -> unit) =
    static let mutable nextSeq = 0L
    static let getSeq () =
        let seq = nextSeq
        nextSeq <- nextSeq + 1L
        seq

    let seq = getSeq ()

    let compareEntries (x : Entry) (y : Entry) =
        let answer = compare x.Rank y.Rank
        if answer = 0 then compare x.Seq y.Seq
        else answer

    member __.Rank : Node = rank
    member __.Action : Transaction -> unit = action
    member __.Seq = seq

    override this.Equals(otherObj) =
        match otherObj with
        | :? Entry as other -> this.Rank = other.Rank
        | _ -> false

    override this.GetHashCode() = hash this.Rank

    interface System.IComparable<Entry> with
        member this.CompareTo other = compareEntries this other

    interface System.IComparable with
        member this.CompareTo otherObj =
            match otherObj with
            | :? Entry as other -> compareEntries this other
            | _ -> invalidArg "other" "Cannot compare values of different types."

and [<AbstractClass>] internal Node(rank : int64) =
    static let rec ensureBiggerThan (node : Node) limit (visited : HashSet<Node>) =
        if node.Rank > limit || visited.Contains(node) then false
        else
            visited.Add(node) |> ignore
            node.Rank <- (limit + 1L)
            for n in node.GetListenerNodesUnsafe() do
                ensureBiggerThan n node.Rank visited |> ignore
            true

    static let listenersLock = obj()

    static member EnsureBiggerThan = ensureBiggerThan
    static member ListenersLock = listenersLock

    member val Rank = rank with get,set

    abstract member GetListenerNodesUnsafe : unit -> Node list

    override this.Equals(otherObj) =
        match otherObj with
        | :? Node as other -> this.Rank = other.Rank
        | _ -> false

    override this.GetHashCode() = hash this.Rank

    interface System.IComparable<Node> with
        member this.CompareTo other = compare this.Rank other.Rank

    interface System.IComparable with
        member this.CompareTo otherObj =
            match otherObj with
            | :? Node as other -> compare this.Rank other.Rank
            | _ -> invalidArg "otherObj" "Cannot compare values of different types."

and [<AbstractClass>] internal Target(node : Node) =
    member this.Node = node

and internal 'T Node(rank : int64) =
    inherit Node(rank)

    let listeners = List<'T Target>()

    static member Null = Node<'T>(Int64.MaxValue)

    member this.Link action target =
        lock Node.ListenersLock (fun () ->
            let changed = Node.EnsureBiggerThan target this.Rank (HashSet<Node>())
            let t = new Target<'T>(action,target)
            listeners.Add(t)
            (changed,t))

    member this.Unlink = this.RemoveListener
    member this.GetListeners () = lock Node.ListenersLock (fun () -> List.ofSeq(listeners))
    member this.RemoveListener l = lock Node.ListenersLock (fun () -> listeners.Remove(l) |> ignore)

    override this.GetListenerNodesUnsafe () = Seq.map (fun (t : 'T Target) -> t.Node) listeners |> List.ofSeq

and internal 'T Target(action : Transaction -> 'T -> unit, node : Node) =
    inherit Target(node)
    member val Action = new WeakReference<Transaction -> 'T -> unit>(action) with get