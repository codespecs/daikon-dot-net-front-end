Module QuickSorter
    Sub qsort(ByRef a() As Object)
        Dim i
        Dim ii
        For i = 0 To a.Length() - 1
            ii = New System.Random().Next(0, a.Length() - 1)
            If i <> ii Then
                swap(a(i), a(ii))
            End If
        Next

        quicksort(a, 0, a.Length())
    End Sub

    Sub print(ByRef a() As Object)
        Dim i
        For i = 0 To a.Length() - 1
            System.Console.Write("  " & a(i))
        Next
        System.Console.WriteLine()
    End Sub

    Sub main()
        Dim towns() As Object = {"Paris", "London", "Stockholm", "Berlin", "Oslo", "Rome", _
        "Madrid", "Tallinn", "Amsterdam", "Dublin"}

        System.Console.Write("towns before qsort: ")
        print(towns)

        qsort(towns)

        System.Console.Write("towns after qsort: ")
        print(towns)

        Dim numbers() As Object = {5, 7, 5, 2, 8, 4, 6, 2, 3, 5, 1, 8, 5, 7, 3}

        System.Console.Write("numbers before qsort: ")
        print(numbers)

        qsort(numbers)

        System.Console.Write("numbers after qsort: ")
        print(numbers)
    End Sub

    Sub quicksort(ByRef a() As Object, ByVal left As Integer, ByVal right As Integer)
        Dim pivot As Integer
        If right - left > 1 Then
            pivot = getpivot(a, left, right)
            pivot = partition(a, left, right, pivot)
            quicksort(a, left, pivot)
            quicksort(a, pivot + 1, right)
        End If
    End Sub

    Function getpivot(ByRef a() As Object, ByVal left As Integer, ByVal right As Integer)
        Return New System.Random().Next(left, right - 1)
    End Function

    Function partition(ByRef a() As Object, ByVal left As Integer, _
       ByVal right As Integer, ByRef pivot As Integer)
        Dim i
        Dim piv
        Dim store

        piv = a(pivot)

        swap(a(right - 1), a(pivot))

        store = left
        For i = left To right - 2
            If a(i) <= piv Then
                swap(a(store), a(i))
                store = store + 1
            End If
        Next
        swap(a(right - 1), a(store))

        Return store
    End Function

    Sub swap(ByRef v1, ByRef v2)
        Dim tmp
        tmp = v1
        v1 = v2
        v2 = tmp
    End Sub

End Module
