﻿namespace Angara.Execution

open System
open System.Threading
open Angara.Graph
open Angara.StateMachine
open Angara.RuntimeContext
open Angara.Option
open Angara.Trace

type Artefact = obj
type MethodCheckpoint = obj
type Output = 
    | Full    of Artefact list
    | Partial of (Artefact option) list
    member x.TryGet (outRef:OutputRef) =
        match x with
        | Full artefacts -> Some(artefacts.[outRef])
        | Partial artefacts -> if outRef < artefacts.Length then artefacts.[outRef] else None

[<AbstractClass>]
type Method(inputs: Type list, outputs: Type list) =

    interface IVertex with
        member x.Inputs = inputs
        member x.Outputs = outputs
    
    interface IComparable with
        member x.CompareTo(obj: obj): int = 
            failwith "Not implemented yet"

    override x.Equals(obj: obj) = 
        failwith "not implemented"

    override x.GetHashCode() = 
        failwith "not implemented"
                
    abstract ExecuteFrom : Artefact list * MethodCheckpoint option -> (Artefact list * MethodCheckpoint) seq
    abstract Reproduce: Artefact list * MethodCheckpoint -> Artefact list
       

[<Class>] 
type MethodVertexData (output: Output, checkpoint: MethodCheckpoint option) =
    interface IVertexData with
        member x.Contains(outRef) = 
            match output with
            | Output.Full art -> outRef < art.Length
            | Output.Partial art -> outRef < art.Length && art.[outRef].IsSome

        member x.TryGetShape(outRef) = 
            x.TryGet outRef |> Option.map (function
                | :? Array as a when a.Rank = 1 -> a.Length
                | _ -> 0)
        
    member x.Output = output
    member x.Checkpoint = checkpoint

    member x.TryGet(outRef) : Artefact option = 
        match output with
        | Output.Full art when outRef < art.Length -> art.[outRef] |> Some
        | Output.Partial art when outRef < art.Length -> art.[outRef]
        | _ -> None
    
    static member Empty = MethodVertexData(Partial [], None)

 
type Input = 
    | NotAvailable
    | Item of Artefact
    | Array of Artefact array 

module Artefacts =
    open Angara.Option
    open Angara.Data
    
    let internal tryGetOutput (edge:Edge<Method>) (i:VertexIndex) (state:DataFlowState<Method,VertexState<MethodVertexData>>) : Artefact option =
        opt {
            let! vs = state |> Map.tryFind edge.Source
            let! vis = vs |> MdMap.tryFind i
            let! data = vis.Data
            return! data.Output.TryGet edge.OutputRef
        }

    /// Sames as getOutput, but "i" has rank one less than rank of the source vertex,
    /// therefore result is an array of artefacts for all available indices complementing "i".
    let internal tryGetReducedOutput (edge:Edge<_>) (i:VertexIndex) (state:DataFlowState<'v,VertexState<MethodVertexData>>) : Artefact[] option =
        match state |> Map.tryFind edge.Source with
        | Some svs ->
            let r = i.Length
            let items = 
                svs 
                |> MdMap.startingWith i 
                |> MdMap.toSeq 
                |> Seq.filter(fun (j,_) -> j.Length = r + 1) 
                |> Seq.map(fun (j,vis) -> (List.last j, vis.Data |> Option.bind(fun data -> data.Output.TryGet edge.OutputRef))) 
                |> Seq.toList
            let n = items |> Seq.map fst |> Seq.max
            let map = items |> Map.ofList
            let artefacts = Seq.init (n+1) (fun i -> i) |> Seq.map(fun i -> match map.TryFind(i) with | Some(a) -> a | None -> None) |> Seq.toList
            if artefacts |> List.forall Option.isSome then artefacts |> Seq.map Option.get |> Seq.toArray |> Option.Some
            else None
        | None -> None

    /// Returns the vertex' output artefact as n-dimensional typed jagged array, where n is a rank of the vertex.
    /// If n is zero, returns the typed object itself.
    let getMdOutput (v:'v) (outRef: OutputRef) (graph:DataFlowGraph<'v>, state:DataFlowState<'v,VertexState<MethodVertexData>>) = 
        let vector = state |> Map.find v |> MdMap.map (fun vis -> vis.Data.Value.Output.TryGet(outRef).Value)
        let rank = vertexRank v graph.Structure
        if rank = 0 then 
            vector |> MdMap.find []
        else 
            let objArr = MdMap.toJaggedArray vector
            let artType = v.Outputs.[outRef]
            let arrTypes = Seq.unfold(fun (r : int, t : System.Type) ->
                if r > 0 then Some(t, (r-1, t.MakeArrayType())) else None) (rank, artType) |> Seq.toList

            let rec cast (eltType:System.Type) (r:int) (arr:obj[]) : System.Array =
                let n = arr.Length
                if r = 1 then
                    let tarr = System.Array.CreateInstance(eltType, n)
                    if n > 0 then arr.CopyTo(tarr, 0)
                    tarr
                else             
                    let tarr = System.Array.CreateInstance(arrTypes.[r-1], n)
                    for i = 0 to n-1 do tarr.SetValue(cast eltType (r-1) (arr.[i]:?>obj[]), i)
                    tarr
            upcast cast artType rank objArr

    /// <summary>Collects inputs for the vertex slice; represents the reduced input as an array.</summary>
    /// <returns>An array of input snapshots, element of an array corresponds to the respective input port.
    /// If element is NotAvailable, no artefact(s) exists for that input in the state.
    /// If element is Item, there is a single artefact for the input.
    /// If element is Array, there are a number of artefacts that altogether is an input (i.e. `reduce` or `collect` input edges).
    /// </returns>
    let getInputs (state: DataFlowState<Method,VertexState<_>>, graph: DataFlowGraph<Method>) (v:Method, i:VertexIndex) : Input[] =
        let inputTypes = (v :> IVertex).Inputs
        let inputs = Array.init inputTypes.Length (fun i -> if inputTypes.[i].IsArray then Input.Array (Array.empty) else Input.NotAvailable)
        graph.Structure.InEdges v
        |> Seq.groupBy (fun e -> e.InputRef)
        |> Seq.iter (fun (inRef, edges) -> 
               let reduceToEdgeIndex (edge:Edge<'v>) = List.ofMaxLength (edgeRank edge)

               match List.ofSeq edges with
               | [] -> inputs.[inRef] <- Input.NotAvailable

               | [edge] -> // single input edge
                    let edgeIndex = i |> reduceToEdgeIndex edge
                    match edge.Type with
                    | Collect (_,_) -> 
                        match tryGetOutput edge edgeIndex state with
                        | None -> inputs.[inRef] <- Input.NotAvailable
                        | Some(a) -> inputs.[inRef] <- Input.Array [| a |]

                    | Scatter _ ->
                        match tryGetOutput edge (edgeIndex |> List.removeLast) state with
                        | None -> inputs.[inRef] <- Input.NotAvailable
                        | Some(a) -> inputs.[inRef] <- Input.Item ((a:?>System.Array).GetValue(List.last edgeIndex))

                    | Reduce _ ->
                        match tryGetReducedOutput edge edgeIndex state with
                        | None -> inputs.[inRef] <- Input.NotAvailable
                        | Some(a) -> inputs.[inRef] <- Input.Array a

                    | OneToOne _ -> 
                        match tryGetOutput edge edgeIndex state with
                        | None -> inputs.[inRef] <- Input.NotAvailable
                        | Some(a) -> inputs.[inRef] <- Input.Item a

               | edges -> // All multiple input edges have type "Collect" due to type check on connection
                   let index (e : IEdge<'v>) = 
                       match (e :?> Edge<'v>).Type with
                       | Collect (idx,_) -> idx
                       | _ -> failwith "Expecting 'Collect' input edge but the edge has different type"
               
                   let artefacts = 
                       edges
                       |> Seq.map (fun e -> 
                             let edgeIndex = i |> reduceToEdgeIndex e
                             index e, tryGetOutput e edgeIndex state)
                       |> Array.ofSeq
               
                   if artefacts |> Seq.forall (fun (_, a) -> Option.isSome a) then 
                       let artefacts = 
                           artefacts
                           |> Seq.sortBy fst
                           |> Seq.map (snd >> Option.get)
                           |> Array.ofSeq
                       inputs.[inRef] <- Input.Array(artefacts)
                   else inputs.[inRef] <- Input.NotAvailable)
        inputs 

//////////////////////////////////////////////
// 
// Changes Analysis
//
//////////////////////////////////////////////

type internal RuntimeAction<'v> =
    | Delay     of 'v * VertexIndex * TimeIndex
    | Execute   of 'v * VertexIndex * TimeIndex 
    | Reproduce of 'v * VertexIndex * TimeIndex 
    | StopMethod of 'v * VertexIndex * TimeIndex
    | Remove    of 'v

module Analysis =
    /// Analyzes state machine changes and provides a list of action to be performed by an execution runtime.
    /// All methods are executed when have status "CanStart".
    let internal analyzeChanges (state: State<'v,'d>, changes: Changes<'v,'d>) : RuntimeAction<'v> list =
        failwith ""
    //            let processItemChange (v: 'v, i:VertexIndex) (oldvis: (VertexItemState) option) (vis: VertexItemState) : RuntimeAction option =
    //                let oldStatus = 
    //                    match oldvis with
    //                    | Some oldvis -> oldvis.Status
    //                    | None -> Incomplete IncompleteReason.UnassignedInputs
    //
    //                match oldStatus, vis.Status with
    //                | CanStart t1, CanStart t2 when t1 <> t2 -> Delay (v,i,t2) |> Some
    //
    //                | _, CanStart t ->                          Delay (v,i,t) |> Some
    //            
    //                | CanStart _, Started t ->                  Execute (v,i,t,None) |> Some
    //
    //                | CompleteStarted (k0,_,t1), CompleteStarted (k1,_,t2) when k0=k1 && t1=t2 -> None
    //            
    //                | _, CompleteStarted (k,_,t) -> // executes the transient method and sends "Succeeded"/"Failed" when it finishes
    //                    if k.IsSome then failwith "Transient iterative methods are not supported" 
    //                    Execute (v,i,t,None) |> Some
    //
    //                | Complete (Some(_),_), Continues (k,_,t) -> // resumes an iterative method
    //                    let initial = match k with 0 -> None | _ -> Some(k, vis |> output |> Array.ofList)
    //                    Execute (v,i,t,initial) |> Some
    //
    //                | Continues (_,_,t), Complete _     // stops iterations
    //                | Started t, Incomplete (IncompleteReason.Stopped) -> StopMethod (v,i,t) |> Some
    //
    //                | _,_ -> None
    //
    //            let processChange (v : 'v) (change : VertexChanges) : RuntimeAction list =             
    //                Trace.Runtime.TraceEvent(Trace.Event.Verbose, 0, "Processing change of " + v.ToString() + ": " + (change.ToString()))
    //
    //                match change with
    //                | Removed -> [ Remove v ]
    //
    //                | New vs -> 
    //                    vs.ToSeq() |> Seq.choose(fun (i, vis) ->
    //                        match vis.Status with
    //                        | CanStart time -> Delay(v,i,time) |> Some
    //                        | Started startTime -> Execute (v,i,startTime,None) |> Some
    //                        | _ -> None) |> Seq.toList
    //
    //                | Modified (indices,oldvs,newvs,_) ->
    //                    indices |> Set.toSeq |> Seq.choose(fun i -> 
    //                        let vis = newvs.TryGetItem i |> Option.get
    //                        let oldVis = oldvs.TryGetItem i 
    //                        processItemChange (v,i) oldVis vis) |> Seq.toList
    //
    //                | ShapeChanged(oldvs, newvs, isConnectionChanged) ->
    //                    let oldVis i = oldvs.TryGetItem i 
    //                    newvs.ToSeq() |> Seq.choose(fun (i,vis) ->                    
    //                        processItemChange (v,i) (oldVis i) vis) |> Seq.toList
    //            
    //            changes |> Map.fold(fun actions v vc -> (processChange v vc) @ actions) []

//////////////////////////////////////////////
// 
// Runtime
//
//////////////////////////////////////////////

open Angara
open Angara.Observable
open System.Collections.Generic
open System.Threading.Tasks.Schedulers

type internal Progress<'v>(v:'v, i:VertexIndex, progressReported : ObservableSource<'v*VertexIndex*float>) =
    interface IProgress<float> with
        member x.Report(p: float) = progressReported.Next(v,i,p)

// TODO: it is the Scheduler who should prepare the RuntimeContext for the target function.
[<AbstractClass>]
type Scheduler() =
    abstract Start : (unit -> unit) -> unit

    static member ThreadPool() : Scheduler = upcast ThreadPoolScheduler()
        
and [<Class>] ThreadPoolScheduler() =
    inherit Scheduler()

    static let scheduler = LimitedConcurrencyLevelTaskScheduler(System.Environment.ProcessorCount)
    static do Trace.Runtime.TraceInformation(sprintf "ThreadPoolScheduler limits concurrency level with %d" scheduler.MaximumConcurrencyLevel)
    static let mutable ExSeq = 0L   
    
    override x.Start (f: unit -> unit) =
        let id = System.Threading.Interlocked.Increment(&ExSeq)        
        try
            Trace.Runtime.TraceEvent(Trace.Event.Start, RuntimeId.SchedulerExecution, sprintf "Execution %d started" id)
            f()
            Trace.Runtime.TraceEvent(Trace.Event.Stop, RuntimeId.SchedulerExecution, sprintf "Execution %d finished" id)
        with ex ->
            Trace.Runtime.TraceEvent(Trace.Event.Error, RuntimeId.SchedulerExecution, sprintf "Execution %d failed: %O" id ex)
            raise ex

[<Sealed>]
type Runtime (source:IObservable<State<Method, MethodVertexData> * RuntimeAction<Method> list>, scheduler : Scheduler) =
    let messages = ObservableSource<Message<Method, MethodVertexData>>()
    let cancels = Dictionary<Method*VertexIndex,CancellationTokenSource>()
    let progressReported = ObservableSource<Method*VertexIndex*float>()

    let progress (v:Method) (i:VertexIndex) : IProgress<float> = new Progress<Method>(v, i, progressReported) :> IProgress<float>
    
    let cancel (v:Method, i:VertexIndex) (cancels:Dictionary<Method*VertexIndex,CancellationTokenSource>) = 
        match cancels.ContainsKey (v,i) with
        | true -> 
            let cts = cancels.[v,i]
            Trace.Runtime.TraceEvent(Trace.Event.Verbose, 0, sprintf "Canceling delay or execution of %O.%A" v i)
            cts.Cancel()
            cancels.Remove(v,i) |> ignore
        | _ -> ()

    let cancelAll v (cancels:Dictionary<Method*VertexIndex,CancellationTokenSource>) = 
        let indices = cancels |> Seq.choose(fun k -> let u,i = k.Key in if u = v then Some(i) else None) |> Seq.toArray
        indices |> Seq.iter (fun i -> cancel (v,i) cancels)

    let delay (v,i) time =
        let delayMs = 0                    
        let run() = 
            Trace.Runtime.TraceEvent(Trace.Event.Verbose, 0, sprintf "Starting %O.%A at %d" v i time)
            messages.Next(Message.Start ({ Vertex = v; Index = Some i; CanStartTime = Some time }, ignore))
        if delayMs > 0 then
            let cts = new CancellationTokenSource()
            cancels.Add((v,i), cts)
            Async.Start(
                async {
                    do! Async.Sleep(delayMs)
                    if not (cts.IsCancellationRequested) then run() else ()
                }, cts.Token)
        else run()
       
    let postIterationSucceeded v index time result =
        Message.Succeeded { Vertex = v; Index = index; StartTime = time; Result = SucceededResult.IterationResult result }
        |> messages.Next
        
    let postNoMoreIterations v index time = 
        Message.Succeeded { Vertex = v; Index = index; StartTime = time; Result = SucceededResult.NoMoreIterations }
        |> messages.Next

    let postFailure v index time exn = 
        Message.Failed { Vertex = v; Index = index; StartTime = time; Failure = exn }
        |> messages.Next
    
    let convertArrays (inpTypes:Type list) (inputs:Input[]) =
        let restore (inp:Input, inpType:Type) : Artefact = 
            match inp with
            | Array array -> 
                let elType = inpType.GetElementType()
                let typedArr = Array.CreateInstance(elType, array.Length)
                for i in 0..array.Length-1 do
                    typedArr.SetValue(array.[i], i)
                upcast typedArr
            | Item item   -> item
            | NotAvailable -> failwith "Input artefacts for the method to execute are not available"
        Seq.zip inputs inpTypes |> Seq.map restore |> List.ofSeq

    let buildEvaluation (v:Method,index,time,state:State<Method,MethodVertexData>) (cts:CancellationTokenSource) = fun() ->
        Trace.Runtime.TraceEvent(Event.Start, RuntimeId.Evaluation, sprintf "Starting evaluation of %O.[%A]" v index)
                
        let cancellationToken = cts.Token
        RuntimeContext.replaceContext // todo: move to the scheduler
            { Token = cancellationToken
              ProgressReporter = progress v index } |> ignore

        let inputs = (v, index) |> Artefacts.getInputs (state.FlowState, state.Graph)
        try
            let checkpoint =
                opt {
                    let! vis = state.FlowState |> DataFlowState.tryGet (v,index)
                    let! data = vis.Data
                    return! data.Checkpoint
                } 
            let inArtefacts = inputs |> convertArrays (v:>IVertex).Inputs

            v.ExecuteFrom(inArtefacts, checkpoint)
            |> Seq.takeWhile (fun _ -> not cancellationToken.IsCancellationRequested)
            |> Seq.iteri (fun i (output,chk) -> MethodVertexData(Output.Full output, Some chk) |> postIterationSucceeded v index time)
                
            if not cancellationToken.IsCancellationRequested then 
                postNoMoreIterations v index time

            Trace.Runtime.TraceEvent(Event.Stop, RuntimeId.Evaluation, sprintf "SUCCEEDED execution of %O.[%A]" v index)
        with ex -> 
            Trace.Runtime.TraceEvent(Event.Error, RuntimeId.Evaluation, sprintf "FAILED execution of %O.[%A]" v index)
            ex |> postFailure v index time

    let buildReproduce (v:Method,index,time,state:State<Method,MethodVertexData>) = fun() ->
        Trace.Runtime.TraceEvent(Event.Start, RuntimeId.Evaluation, sprintf "Reproducing of %O.[%A]" v index)
                
        let cancellationToken = cts.Token
        RuntimeContext.replaceContext // todo: move to the scheduler
            { Token = cancellationToken
              ProgressReporter = progress v index } |> ignore

        let inputs = (v, index) |> Artefacts.getInputs (state.FlowState, state.Graph)
        try
            let checkpoint =
                opt {
                    let! vis = state.FlowState |> DataFlowState.tryGet (v,index)
                    let! data = vis.Data
                    return! data.Checkpoint
                } 
            let inArtefacts = inputs |> convertArrays (v:>IVertex).Inputs

            let outArtefacts = v.Reproduce(inArtefacts, checkpoint)
            MethodVertexData(Output.Full outArtefacts, checkpoint)
            |> postIterationSucceeded v index time             

            Trace.Runtime.TraceEvent(Event.Stop, RuntimeId.Evaluation, sprintf "SUCCEEDED reproducing of %O.[%A]" v index)
        with ex -> 
            Trace.Runtime.TraceEvent(Event.Error, RuntimeId.Evaluation, sprintf "FAILED reproducing of %O.[%A]" v index)
            ex |> postFailure v index time

    let performAction (state : State<Method,MethodVertexData>) (action : RuntimeAction<Method>) = 
        match action with
        | Delay (v,slice,time) -> 
            cancel (v,slice) cancels
            delay (v,slice) time

        | StopMethod (v,slice,time) -> 
            cancel (v,slice) cancels

        | Execute (v,slice,time) -> 
            let cts = new CancellationTokenSource()
            let func = buildEvaluation (v,slice,time,state) cts 
            scheduler.Start func
            cancel (v,slice) cancels
            cancels.Add((v,slice), cts)

        | Reproduce (v,slice,time) -> failwith ""
            

        | Remove v -> 
            cancelAll v cancels

    let perform (state : State<_,_>, actions : RuntimeAction<_> list) = 
        try
            actions |> Seq.iter (performAction state) 
        with exn ->
            Trace.Runtime.TraceEvent(Trace.Event.Critical, 0, sprintf "Execution runtime failed: %O" exn)
            messages.Error exn

    let mutable subscription = null

    do
        subscription <- source |> Observable.subscribe perform 


    interface IDisposable with
        member x.Dispose() = 
            subscription.Dispose()
            cancels.Values |> Seq.iter(fun t -> t.Cancel(); (t :> IDisposable).Dispose())
            messages.Completed()
            progressReported.Completed()

    member x.Evaluation = messages.AsObservable
    member x.Progress = progressReported.AsObservable


module internal Helpers = 
    let internal asAsync (create: ReplyChannel<_> -> Message<_,_>) (source:ObservableSource<_>) : Async<Response<_>> = 
        Async.FromContinuations(fun (ok, _, _) -> create ok |> source.Next)

    let internal unwrap (r:Async<Response<_>>) : Async<_> = 
        async{
            let! resp = r
            return match resp with Success s -> s | Exception exn -> raise exn
        }

open Helpers

[<Class>]
type Engine(graph:DataFlowGraph<Method>, state:DataFlowState<Method,VertexState<MethodVertexData>>, scheduler:Scheduler) =
    
    let messages = ObservableSource()

    let matchOutput (a:MethodVertexData) (b:MethodVertexData) (outRef:OutputRef) = 
        match a.TryGet outRef, b.TryGet outRef with
        | Some artA, Some artB -> LanguagePrimitives.GenericEqualityComparer.Equals(a,b)
        | _ -> false
            
    let stateMachine = Angara.StateMachine.CreateSuspended messages.AsObservable matchOutput (graph, state)
    let runtimeActions = stateMachine.Changes |> Observable.map (fun (s,c) -> s, Analysis.analyzeChanges (s,c))
    let runtime = new Runtime(runtimeActions, scheduler)

    let mutable sbs = null

    do
        sbs <- runtime.Evaluation.Subscribe(messages.Next)

    member x.State = stateMachine.State
    member x.Changes = stateMachine.Changes
    member x.Progress = runtime.Progress

    member x.Start() = stateMachine.Start()

    member x.AlterAsync alter =
        asAsync (fun reply -> Alter(alter, reply)) messages |> unwrap

    interface IDisposable with
        member x.Dispose() = 
            sbs.Dispose()
            (stateMachine :> IDisposable).Dispose()
            (runtime :> IDisposable).Dispose()

