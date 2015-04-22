# Celeriac: the .NET Front-End for Daikon

[![Build status](https://ci.appveyor.com/api/projects/status/t6tbkmsa1dababnu?svg=true)](https://ci.appveyor.com/project/twschiller/daikon-dot-net-front-end)

The [Daikon dynamic invariant detector](http://plse.cs.washington.edu/daikon/) uses machine learning to infer likely program invariants and properties. The types of properties inferred by Daikon include “.field > abs(y)”; “y = 2*x+3”; “array a is sorted”; “for all list objects lst, lst.next.prev = lst”; “for all treenode objects n, n.left.value < n.right.value”; “p != null ⇒ p.content in myArray”; and many more.

This project, Celeriac, dynamically instruments a .NET application to produce a Daikon-compatible program trace. Celeriac works directly on binaries, and therefore does not require build modifications.

To start using Celeriac, visit the [Getting Started](https://github.com/melonhead901/daikon-dot-net-front-end/wiki) wiki page. (We are in the process of migrating the documentation over to the GitHub Wiki).

Binaries are available via [AppVeyor](https://ci.appveyor.com/project/twschiller/daikon-dot-net-front-end/build/artifacts).
