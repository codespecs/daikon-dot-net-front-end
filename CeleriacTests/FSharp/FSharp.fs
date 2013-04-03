// Learn more about F# at http://fsharp.net
open System

let set = ref Set.empty;

set := (!set).Add(0)




let addMax (set:Set<_>) = 
  let max = Seq.max set in
  set.Add(max+1)

set := addMax(!set)
set := addMax(!set)
set := addMax(!set)
set := addMax(!set)
set := addMax(!set)


let printCollection msg coll =
  printfn "%s:" msg
  Seq.iteri (fun index item -> printfn "  %i: %O" index item) coll

printCollection "Collection" !set
