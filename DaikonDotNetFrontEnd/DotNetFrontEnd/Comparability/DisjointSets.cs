using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics.Contracts;
using DotNetFrontEnd.Contracts;

namespace EmilStefanov
{
  public class DisjointSets
  {
    [ContractInvariantMethod]
    private void ObjectInvariant()
    {
      Contract.Invariant(this.m_elementCount >= 0);
      Contract.Invariant(this.m_nodes.Count == this.m_elementCount);
      Contract.Invariant(this.m_setCount >= 0 && this.m_setCount <= this.m_elementCount);
    }

    /// <summary>
    /// Create an empty DisjointSets data structure
    /// </summary>
    public DisjointSets()
      : this(0)
    {
    }

    /// <summary>
    /// Create a DisjointSets data structure with a specified number of elements (with element id's from 0 to count-1)
    /// </summary>
    /// <param name="count"></param>
    public DisjointSets(int count)
    {
      Contract.Requires(count >= 0);

      m_elementCount = 0;
      m_setCount = 0;
      m_nodes = new List<Node>();
      AddElements(count);
    }

    /// <summary>
    /// Find the set identifier that an element currently belongs to.
    /// </summary>
    /// <param name="element"></param>
    /// <remarks>Some internal data is modified for optimization even though this method is consant.</remarks>
    /// <returns></returns>
    [Pure]
    public int FindSet(int elementId)
    {
      Contract.Requires(elementId >= 0 && elementId <= this.ElementCount);
      Contract.Ensures(Contract.Result<int>() >= 0 && Contract.Result<int>() <= this.ElementCount);

      Node curNode;

      // Find the root element that represents the set which `elementId` belongs to
      curNode = m_nodes[elementId];
      while (curNode.Parent != null)
        curNode = curNode.Parent;
      Node root = curNode;

      // Walk to the root, updating the parents of `elementId`. Make those elements the direct
      // children of `root`. This optimizes the tree for future FindSet invokations.
      curNode = m_nodes[elementId];
      while (curNode != root)
      {
        Node next = curNode.Parent;
        curNode.Parent = root;
        curNode = next;
      }

      return root.Index;
    }

    /// <summary>
    /// Combine two sets into one. All elements in those two sets will share the same set id that can be gotten using FindSet.
    /// </summary>
    /// <param name="setId1"></param>
    /// <param name="setId2"></param>
    public bool Union(int setId1, int setId2)
    {
      Contract.Requires(setId1 >= 0 && setId1 <= ElementCount);
      Contract.Requires(setId2 >= 0 && setId2 <= ElementCount);
      Contract.Ensures(Contract.Result<bool>().Implies(SetCount == Contract.OldValue(SetCount) - 1));

      if (setId1 == setId2)
      {
        return false;
      }

      Node set1 = m_nodes[setId1];
      Node set2 = m_nodes[setId2];

      // Determine which node representing a set has a higher rank. The node with the higher rank is
      // likely to have a bigger subtree so in order to better balance the tree representing the
      // union, the node with the higher rank is made the parent of the one with the lower rank and
      // not the other way around.
      if (set1.Rank > set2.Rank)
        set2.Parent = set1;
      else if (set1.Rank < set2.Rank)
        set1.Parent = set2;
      else // set1.Rank == set2.Rank
      {
        set2.Parent = set1;
        ++set1.Rank; // update rank
      }

      // Since two sets have fused into one, there is now one less set so update the set count.
      --m_setCount;
      return true;
    }

    public int AddElement()
    {
      Contract.Ensures(Contract.Result<int>() == ElementCount - 1);
      Contract.Ensures(ElementCount == Contract.OldValue(ElementCount) + 1);
      Contract.Ensures(SetCount == Contract.OldValue(SetCount) + 1);

      AddElements(1);
      return ElementCount - 1;
    }

    /// <summary>
    /// Add a specified number of elements to the DisjointSets data structure. The element id's of the new elements are numbered
    /// consequitively starting with the first never-before-used elementId.
    /// </summary>
    /// <param name="addCount"></param>
    public void AddElements(int addCount)
    {
      Contract.Requires(addCount >= 0);
      Contract.Ensures(this.ElementCount == Contract.OldValue<int>(this.ElementCount) + addCount);
      Contract.Ensures(this.SetCount == Contract.OldValue<int>(this.SetCount) + addCount);

      // insert and initialize the specified number of element nodes to the end of the `m_nodes` array
      for (int i = m_elementCount; i < m_elementCount + addCount; ++i)
      {
        Node newNode = new Node();
        newNode.Parent = null;
        newNode.Index = i;
        newNode.Rank = 0;
        m_nodes.Add(newNode);
      }

      // update element and set counts
      m_elementCount += addCount;
      m_setCount += addCount;
    }

    /// <summary>
    /// Returns the number of elements currently in the DisjointSets data structure.
    /// </summary>
    public int ElementCount
    {
      get
      {
        Contract.Ensures(Contract.Result<int>() == m_elementCount);
        Contract.Ensures(Contract.Result<int>() >= 0);
        return m_elementCount;
      }
    }

    /// <summary>
    /// Returns the number of sets currently in the DisjointSets data structure.
    /// </summary>
    public int SetCount
    {
      get
      {
        Contract.Ensures(Contract.Result<int>() == m_setCount);
        Contract.Ensures(Contract.Result<int>() >= 0 && Contract.Result<int>() <= ElementCount);
        return m_setCount;
      }
    }

    /// <summary>
    /// Internal Node data structure used for representing an element.
    /// </summary>
    private class Node
    {
      /// <summary>
      /// This roughly represent the max height of the node in its subtree.
      /// </summary>
      public int Rank;

      /// <summary>
      /// The index of the element the node represents.
      /// </summary>
      public int Index;

      /// <summary>
      /// The parent node of the node.
      /// </summary>
      public Node Parent;
    }

    /// <summary>
    /// The number of elements currently in the DisjointSets data structure.
    /// </summary>
    private int m_elementCount;

    /// <summary>
    /// The number of sets currently in the DisjointSets data structure.
    /// </summary>
    private int m_setCount;

    /// <summary>
    /// The list of nodes representing the elements.
    /// </summary>
    private readonly List<Node> m_nodes;
  }
}