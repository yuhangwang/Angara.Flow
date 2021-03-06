﻿namespace Angara.Execution

open System
open Angara.Graph
open Angara.StateMachine

/// Represents an element of a method output.
/// Angara stores method outputs in a flow state and passes them as other methods arguments.
type Artefact = obj

/// Contains data identifying a certain reproducible stage of the execution.
/// Actual type of the checkpoint depends on the Method; a Method may not use the checkpoints and return `null`.
type MethodCheckpoint = obj

/// A graph vertex which can be executed.
[<AbstractClass>]
type Method =
    new : Type list * Type list -> Method

    interface IVertex
    interface IComparable // todo: how to compare methods?

    /// Applies the method to the given input artefacts.
    /// Returns a sequence of checkpoints; for each checkpoint there are output artefacts and the corresponding checkpoint,
    /// which can allow to resume iteration not from the beginning.
    /// If the method doesn't split the execution into the stages identified by checkpoints,
    /// it should returns a sequence of a single element and `null` as checkpoint.
    /// If the checkpoint is null, the Method should start execution from the beginning.
    /// Prohibited:
    /// - return an empty sequence;
    /// - return checkpoint value that doesn't allow to reproduce the corresponding artefacts deterministically.
    abstract Execute : Artefact list * MethodCheckpoint option -> (Artefact list * MethodCheckpoint) seq

/// Represents output artefacts of a method.
/// An artefact can be missing, e.g. if the dataflow snapshot couldn't be restored completely.
type Output = 
    | Full    of Artefact list
    | Partial of (Artefact option) list
    member TryGet : OutputRef -> Artefact option

/// Keeps method evaluation state: output artefacts, inner state, trace information.
[<Class>] 
type MethodVertexData =
    interface IVertexData

    // todo: option for both Output * MethodCheckpoint?

    /// Keeps output artefacts of the vertex. Can be missing, e.g. because of serialization limitations.
    member Output : Output
    /// Contains information sufficient to reproduce the corresponding outputs and continue the execution from the checkpoint.
    /// If `None`, the output hasn't been produced yet.
    member Checkpoint : MethodCheckpoint option

//////////////////////////////////////////////
// 
// Engine
//
//////////////////////////////////////////////

/// Runs methods.
[<AbstractClass>]
type Scheduler =
    abstract Start : (unit -> unit) -> unit
    
    /// Creates a scheduler which runs methods in the thread pool and limits concurrency level
    /// depending on number of CPU cores.
    static member ThreadPool : unit -> Scheduler


/// Represents an engine which maintains a combination of the StateMachine and Runtime.
/// It makes the changes flow from the state machine to the runtime and
/// makes the messages flow from the runtime to the state machine.
[<Class>]
type Engine =
    new : DataFlowGraph<Method> * DataFlowState<Method,VertexState<MethodVertexData>> * Scheduler -> Engine
    interface IDisposable

    member State : State<Method, MethodVertexData>
    member Changes : IObservable<State<Method, MethodVertexData> * Changes<Method, MethodVertexData>>
    /// Allows to see progress of method evaluation.
    member Progress : IObservable<Method * VertexIndex * float>
    member Start : unit -> unit
    
    /// Alters the flow dependency graph by performing the batch of operations in the specific order:
    /// disconnect vertices; remove vertices; merge with another graph; connect vertices.
    /// The operation asynchronously completes when all the operations succeed.
    member AlterAsync : AlterMessage<Method,MethodVertexData> -> Async<unit>

