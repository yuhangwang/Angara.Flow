﻿module StateOperations

open NUnit.Framework
open Angara.Data
open Angara.Graph
open Angara.States
open GraphExpression

let internal exprToGraph = System.Collections.Generic.Dictionary<Graph, Angara.Graph.DataFlowGraph<Vertex> * Map<string, Vertex>>()

let vector (statuses : VertexStatus list) =
    statuses |> Seq.mapi(fun i v -> i,v) |> Seq.fold (fun map (i,v) -> MdMap.add [i] v map) MdMap.empty

let data s = { new IVertexData with member x.Shape = s }

let mdState (graphExpr:Graph, statuses:(string* MdMap<int,VertexStatus>) list, timeIndex : TimeIndex) =
    let flowGraph, nameToVertex = 
        match exprToGraph.TryGetValue graphExpr with
        | true, p -> p
        | false, _ ->
            let p = buildDag graphExpr
            exprToGraph.Add(graphExpr, p)
            p
        
    let flowState : Map<Vertex, MdMap<int,VertexState>> = 
        statuses 
        |> List.fold(fun map (nodeName, status) -> 
            let status2 = status |> MdMap.map(fun st -> { Status = st; Data = None })
            map.Add(nameToVertex.[nodeName], status2)) Map.empty
    { Graph = flowGraph; FlowState = flowState; TimeIndex = timeIndex }, nameToVertex


let state (graphExpr:Graph, statuses:(string*VertexStatus) list, timeIndex : TimeIndex) =
    let mstatuses = statuses |> List.map(fun (n,s) -> n, MdMap.scalar s)
    mdState (graphExpr, mstatuses, timeIndex)

let takeState (stateUpdate : StateUpdate<Vertex>) = stateUpdate.State 

module Message = Angara.States.Messages
let transition = Angara.States.Messages.transition

let mdStart (nodeName : string, i : VertexIndex) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    state |> transition (Message.Start ({ Message.StartMessage.Vertex = nameToVertex.[nodeName];  Message.StartMessage.Index = i;  Message.StartMessage.CanStartTime = None } )), nameToVertex

let start (nodeName : string) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    mdStart (nodeName, []) (state, nameToVertex)

let mdIteration (nodeName : string, i : VertexIndex) (startTime : TimeIndex) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    state |> transition (Message.Iteration { Message.IterationMessage.Vertex = nameToVertex.[nodeName];  Message.IterationMessage.Index = i;  Message.IterationMessage.Result = data [0];  Message.IterationMessage.StartTime = startTime }), nameToVertex

let iteration (nodeName : string) (startTime : TimeIndex) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    mdIteration (nodeName, []) startTime (state, nameToVertex)


let iteration_array (nodeName : string) (startTime : TimeIndex) (shape : int list) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    state |> transition (Message.Iteration { Message.IterationMessage.Vertex = nameToVertex.[nodeName];  Message.IterationMessage.Index = [];  Message.IterationMessage.Result = data shape;  Message.IterationMessage.StartTime = startTime }), nameToVertex
    
let succeeded (nodeName : string) (startTime : TimeIndex) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    state |> transition (Message.Succeeded { Message.SucceededMessage.Vertex = nameToVertex.[nodeName]; Message.SucceededMessage.Index = []; Message.SucceededMessage.StartTime = startTime }), nameToVertex

let stop (nodeName : string) (state : State<Vertex>, nameToVertex: Map<string, Vertex>) =
    state |> transition (Message.Stop { Message.StopMessage.Vertex = nameToVertex.[nodeName]; Message.StopMessage.Index = [] }), nameToVertex


let check (stateExp : State<Vertex>, nameToVertexExp: Map<string, Vertex>) (stateAct : StateUpdate<Vertex>, nameToVertexAct: Map<string, Vertex>) =
    let stateAct = stateAct.State
    Assert.AreEqual(nameToVertexExp, nameToVertexAct, sprintf "Different names (%d)" stateExp.TimeIndex)
    Assert.AreEqual(stateExp.Graph, stateAct.Graph, sprintf "Different graphs (%d)" stateExp.TimeIndex)
    
    let exp = stateExp.FlowState |> Map.toArray
    let act = stateAct.FlowState |> Map.toArray
    Assert.AreEqual(exp.Length, act.Length, sprintf "Different length of states (%d)" stateExp.TimeIndex)
    Array.iter2(fun (v,e) (u,a) -> 
        Assert.AreEqual(v :> obj, u :> obj, sprintf "Different vertices of states (%d)" stateExp.TimeIndex)
        let eq = MdMap.equal(fun i s t -> Assert.AreEqual(s.Status, t.Status, sprintf "Different %A states at index (%A) (%d)" v i stateExp.TimeIndex); true) e a
        Assert.IsTrue(eq, sprintf "Different shapes of %A states (%d)" v stateExp.TimeIndex)) exp act

    Assert.AreEqual(stateExp.TimeIndex, stateAct.TimeIndex, sprintf "Different time indices (%d)" stateExp.TimeIndex)
    (stateAct, nameToVertexAct)

