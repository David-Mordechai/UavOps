"""Fuses the hand-built layer norms of a HuggingFace DETR-family ONNX export (ReduceMean, Sub, Pow,
ReduceMean, Add eps, Sqrt, Div, Mul weight, Add bias) into ONNX's own LayerNormalization (opset 17).

Why: TensorRT builds the unfused pattern in FP16, where the variance overflows. Measured on
D-FINE-S (Objects365) on the Jetson Orin Nano: the FP16 engine scored the COCO cats photo's cats
0.30 (FP32: 0.94) and found no vehicles at all. TensorRT maps LayerNormalization to its
normalization layer, which stays accurate in an FP16 engine.

    python fuse_layernorm.py model.onnx model.ln.onnx
"""
import sys

import numpy as np
import onnx
from onnx import helper, numpy_helper

src, dst = sys.argv[1], sys.argv[2]
m = onnx.load(src)
g = m.graph
consts = {i.name: numpy_helper.to_array(i) for i in g.initializer}
for n in g.node:
    if n.op_type == "Constant":
        consts[n.output[0]] = numpy_helper.to_array(n.attribute[0].t)
producer = {o: n for n in g.node for o in n.output}

fused, remove, add = 0, set(), []
for sqrt in [n for n in g.node if n.op_type == "Sqrt"]:
    add_eps = producer.get(sqrt.input[0])
    rm2 = producer.get(add_eps.input[0]) if add_eps is not None else None
    pow_ = producer.get(rm2.input[0]) if rm2 is not None else None
    sub = producer.get(pow_.input[0]) if pow_ is not None else None
    rm1 = producer.get(sub.input[1]) if sub is not None else None
    if None in (add_eps, rm2, pow_, sub, rm1) or [x.op_type for x in (add_eps, rm2, pow_, sub, rm1)] != ["Add", "ReduceMean", "Pow", "Sub", "ReduceMean"]:
        continue
    div = next(n for n in g.node if n.op_type == "Div" and sqrt.output[0] in n.input)
    mul = next(n for n in g.node if n.op_type == "Mul" and div.output[0] in n.input)
    bias_add = next(n for n in g.node if n.op_type == "Add" and mul.output[0] in n.input)
    weight = next(i for i in mul.input if i != div.output[0])
    bias = next(i for i in bias_add.input if i != mul.output[0])
    eps = float(np.asarray(consts[next(i for i in add_eps.input if i != rm2.output[0])]).reshape(-1)[0])
    x = sub.input[0]
    add.append((bias_add, helper.make_node("LayerNormalization", [x, weight, bias], [bias_add.output[0]],
                                           name=sqrt.name.rsplit("/", 1)[0] + "/LayerNormalization", axis=-1, epsilon=eps)))
    remove.update(id(n) for n in (rm1, sub, pow_, rm2, add_eps, sqrt, div, mul, bias_add))
    fused += 1

# Insert each fused node where its last original node was, keeping the graph topologically sorted.
nodes = []
replacement = {id(old): new for old, new in add}
for n in g.node:
    if id(n) in replacement:
        nodes.append(replacement[id(n)])
    elif id(n) not in remove:
        nodes.append(n)
del g.node[:]
g.node.extend(nodes)
for o in m.opset_import:
    if o.domain in ("", "ai.onnx"):
        o.version = max(o.version, 17)
onnx.checker.check_model(m)
onnx.save(m, dst)
print(f"fused {fused} layer norms -> {dst}")
