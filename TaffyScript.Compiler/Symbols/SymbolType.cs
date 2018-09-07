﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TaffyScript.Compiler
{
    public enum SymbolType
    {
        Block,
        Object,
        Enum,
        Script,
        Namespace,
        Variable,
        Property,
        Field
    }
}
