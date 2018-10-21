---
layout: default
title: Map.zip
---

# Map.zip

[\[global\]]({{site.baseurl}}/docs/).[Map]({{site.baseurl}}/docs/Map/).[zip]({{site.baseurl}}/docs/Map/zip/)

_Applies a script to the corresponding elements of two sequences, producing a sequence of the results._

```cs
Map.zip(other, result_selector)
```

## Arguments

<table>
  <col width="15%">
  <col width="15%">
  <thead>
    <tr>
      <th>Argument</th>
      <th>Type</th>
      <th>Description</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>other</td>
      <td>Enumerable</td>
      <td>The other sequence to merge.</td>
    </tr>
    <tr>
      <td>result_selector</td>
      <td>script</td>
      <td>A script that specifies how to merge the elements from the two sequences.</td>
    </tr>
  </tbody>
</table>

**Returns:** Enumerable