﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Reflection.Emit;
using TaffyScript.Reflection;
using TaffyScript.Strings;

namespace TaffyScript.Compiler.Backend
{
    public class AssetStore
    {
        private static FieldInfo _baseMemberField = typeof(TsInstance).GetField("_members", BindingFlags.NonPublic | BindingFlags.Instance);
        private static Type _baseType = typeof(TsInstance);
        private static ConstructorInfo _baseConstructor = typeof(TsInstance).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        private static ConstructorInfo _definitionConstructor = typeof(ObjectDefinition).GetConstructor(new[] { typeof(string),
                                                                                                                typeof(string),
                                                                                                                typeof(Func<TsObject[], ITsInstance>) });

        private Dictionary<ISymbol, ObjectInfo> _definedTypes = new Dictionary<ISymbol, ObjectInfo>();

        private SymbolTable _table;
        private SymbolResolver _resolver;
        private IErrorLogger _logger;
        private ModuleBuilder _module;
        private MsilWeakCodeGen _codeGen;
        private ILEmitter _moduleInitializer;

        public Dictionary<ISymbol, MethodInfo> Globals { get; } = new Dictionary<ISymbol, MethodInfo>();

        public AssetStore(MsilWeakCodeGen codeGen, SymbolTable table, SymbolResolver resolver, IErrorLogger logger)
        {
            _codeGen = codeGen;
            _table = table;
            _logger = logger;
            _resolver = resolver;
        }

        public void InitializeModule(ModuleBuilder module, ILEmitter moduleInitializer)
        {
            _module = module;
            _moduleInitializer = moduleInitializer;
        }

        public ObjectInfo AddExternalType(ISymbol symbol, Type type)
        {
            var result = new ObjectInfo(type, null, null, null, null);
            _definedTypes.Add(symbol, result);
            return result;
        }

        public void AddObjectInfo(ISymbol symbol, ObjectInfo info)
        {
            _definedTypes.Add(symbol, info);
        }

        public Type GetDefinedType(ISymbol typeSymbol, TokenPosition position)
        {
            return GetObjectInfo(typeSymbol, position).Type;
        }

        public MethodInfo GetInstanceMethod(ISymbol typeSymbol, string methodName, TokenPosition position)
        {
            var info = GetObjectInfo(typeSymbol, position);

            if (!info.Methods.TryGetValue(methodName, out var method))
            {
                if (typeSymbol is ObjectSymbol obj)
                {
                    if(obj.Children.TryGetValue(methodName, out var methodSymbol))
                    {
                        if(methodSymbol.Type != SymbolType.Script)
                        {
                            _logger.Error($"Tried to get member that wasn't a script from type '{info.Type.FullName}'");
                            return null;
                        }

                        var builder = info.Type as TypeBuilder;
                        if (methodSymbol.Scope == SymbolScope.Member)
                        {
                            var parentMethod = GetInstanceMethod(obj.Inherits, methodName);

                            if (parentMethod != null && (parentMethod.IsFinal || !(parentMethod.IsVirtual || parentMethod.IsAbstract)))
                            {
                                _logger.Error("Tried to override non-virtual, non-abstract method");
                                return null;
                            }

                            MethodAttributes flags = (parentMethod is null ? MethodAttributes.NewSlot : 0)
                                | MethodAttributes.Virtual
                                | MethodAttributes.Public
                                | MethodAttributes.HideBySig;

                            method = builder.DefineMethod(methodName, flags, typeof(TsObject), TsTypes.ArgumentTypes);
                        }
                        else
                        {
                            MethodAttributes flags = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                            var mBuilder = builder.DefineMethod(methodName, flags, typeof(TsObject), TsTypes.ArgumentTypes);
                            var staticAttribute = new CustomAttributeBuilder(typeof(TaffyScriptStaticAttribute).GetConstructor(Type.EmptyTypes), new object[] { });
                            mBuilder.SetCustomAttribute(staticAttribute);
                            method = mBuilder;
                        }
                    }
                    else if (obj.Inherits != null)
                    {
                        method = GetInstanceMethod(obj.Inherits, methodName, position);
                    }
                    else
                    {
                        _logger.Error("Tried to call script that does not exist", position);
                        return null;
                    }
                }
                else
                {
                    method = info.Type.GetMethod(methodName, BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance, null, TsTypes.ArgumentTypes, null);
                    if (method is null)
                    {
                        _logger.Error("Tried to call script that doesn't exist", position);
                        return null;
                    }
                }
                info.Methods.Add(methodName, method);
            }

            return method;
        }

        private MethodInfo GetInstanceMethod(ISymbol typeSymbol, string methodName)
        {
            if (typeSymbol is null)
                return null;

            var info = GetObjectInfo(typeSymbol, null);
            
            if(!info.Methods.TryGetValue(methodName, out var method))
            {
                if (typeSymbol is ObjectSymbol obj)
                {
                    if (obj.Children.TryGetValue(methodName, out var methodSymbol))
                    {
                        if (methodSymbol.Type != SymbolType.Script)
                            return null;

                        var builder = info.Type as TypeBuilder;
                        if (methodSymbol.Scope == SymbolScope.Member)
                        {

                            MethodAttributes flags = (GetInstanceMethod(obj.Inherits, methodName) is null ? MethodAttributes.NewSlot : 0)
                                | MethodAttributes.Virtual
                                | MethodAttributes.Public
                                | MethodAttributes.HideBySig;

                            method = builder.DefineMethod(methodName, flags, typeof(TsObject), TsTypes.ArgumentTypes);
                        }
                        else
                        {
                            MethodAttributes flags = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                            var mBuilder = builder.DefineMethod(methodName, flags, typeof(TsObject), TsTypes.ArgumentTypes);
                            var staticAttribute = new CustomAttributeBuilder(typeof(TaffyScriptStaticAttribute).GetConstructor(Type.EmptyTypes), new object[] { });
                            mBuilder.SetCustomAttribute(staticAttribute);
                            method = mBuilder;
                        }
                    }
                    else if (obj.Inherits != null)
                        method = GetInstanceMethod(obj.Inherits, methodName);
                    else
                        return null;
                }
                else
                    method = info.Type.GetMethod(methodName, BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance, null, TsTypes.ArgumentTypes, null);

                if(method != null)
                    info.Methods.Add(methodName, method);
            }
            return method;
        }

        public ConstructorInfo GetConstructor(ISymbol typeSymbol, TokenPosition position)
        {
            var info = GetObjectInfo(typeSymbol, position);
            if (info.Constructor is null)
                info.Constructor = info.Type.GetConstructor(new[] { typeof(TsObject[]) });

            return info.Constructor;
        }

        public ObjectInfo GetObjectInfo(ISymbol symbol, TokenPosition position)
        {
            if (!_definedTypes.TryGetValue(symbol, out var info))
            {
                if (symbol is ObjectSymbol os)
                    info = GenerateType(os, position);
                else if (symbol is ImportObjectLeaf leaf)
                    leaf.ImportObject.Accept(_codeGen);
            }

            if (info is null)
                _logger.Error($"Tried to get type that doesn't exist: {_resolver.GetAssetFullName(symbol)}", position);

            return info;
        }

        public void FinalizeType(ObjectInfo info, ISymbol parent, List<MethodInfo> scripts, TokenPosition position)
        {
            var parentConstructor = parent == null ? _baseConstructor : GetConstructor(parent, position);
            var type = (TypeBuilder)info.Type;

            GenerateConstructor(info, parentConstructor, scripts.FirstOrDefault(m => m.Name == "create"));
            GenerateTryGetDelegate(info, scripts);
            var toString = scripts.FirstOrDefault(m => m.Name == "to_string");

            if (toString != null)
                GenerateToStringMethod(type, toString);

            _moduleInitializer.LdStr(info.Type.FullName);
            if (info.Parent is null)
                _moduleInitializer.LdNull();
            else
                _moduleInitializer.LdStr(info.Parent?.Type.FullName);

            _moduleInitializer.LdNull()
                              .LdFtn(info.Create)
                              .New(typeof(Func<TsObject[], ITsInstance>).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))
                              .New(_definitionConstructor)
                              .Call(typeof(TsReflection).GetMethod("ProcessObjectDefinition"));

            var attrib = new CustomAttributeBuilder(typeof(TaffyScriptObjectAttribute).GetConstructor(Type.EmptyTypes), Type.EmptyTypes);
            type.SetCustomAttribute(attrib);
        }

        #region Native TaffyScript Object Generation

        private ObjectInfo GenerateType(ObjectSymbol os, TokenPosition position)
        {
            var typeName = _resolver.GetAssetFullName(os);
            var builder = _module.DefineType(typeName, TypeAttributes.Public);
            builder.AddInterfaceImplementation(typeof(ITsInstance));
            ObjectInfo parent = null;
            FieldInfo members;
            if (os.Inherits != null)
            {
                parent = GetObjectInfo(os.Inherits, position);
                builder.SetParent(parent.Type);
                // Todo: Verify that the parent is valid.
                //       It shouldn't be possible to inherit from a type that
                //       has a sealed TryGetDelegate method.
            }
            else
                builder.SetParent(typeof(TsInstance));

            if (parent != null && !typeof(TsInstance).IsAssignableFrom(parent.Type))
            {
                if(parent.Members is null)
                {
                    //Todo: Memoize the result if it's not found.
                    var inherits = parent.Type;
                    while (inherits is TypeBuilder parentBuilder && !parentBuilder.IsCreated())
                        inherits = inherits.BaseType;
                    members = inherits.GetField("_members", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    parent.Members = members;
                }
                else
                    members = parent.Members;

                if (members == null || members.FieldType != typeof(Dictionary<string, TsObject>))
                    members = builder.DefineField("_members", typeof(Dictionary<string, TsObject>), FieldAttributes.Family);
            }
            else
                members = _baseMemberField;
            
            var tryGetDelegate = builder.DefineMethod("TryGetDelegate",
                                                      MethodAttributes.Public |
                                                          MethodAttributes.HideBySig |
                                                          MethodAttributes.Virtual,
                                                      typeof(bool),
                                                      new[] { typeof(string), typeof(TsDelegate).MakeByRefType() });
            tryGetDelegate.DefineParameter(2, ParameterAttributes.Out, "del");
            var constructor = builder.DefineConstructor(MethodAttributes.Public |
                                                            MethodAttributes.HideBySig |
                                                            MethodAttributes.SpecialName |
                                                            MethodAttributes.RTSpecialName,
                                                        CallingConventions.HasThis,
                                                        new[] { typeof(TsObject[]) });
            GenerateObjectType(builder);
            var info = new ObjectInfo(builder, parent, tryGetDelegate, constructor, members);
            _definedTypes.Add(os, info);

            return info;
        }

        private MethodInfo GetTryGetDelegate(ObjectInfo info)
        {
            if (info == null)
                return null;

            if (info.TryGetDelegate != null)
                return info.TryGetDelegate;

            info.TryGetDelegate = info.Type.GetMethod("TryGetDelegate", new[] { typeof(string), typeof(TsDelegate).MakeByRefType() });
            if (!info.TryGetDelegate.IsVirtual)
                _logger.Error("Tried to inherit from an object with a non-virtual TryGetDelegate method");

            return info.TryGetDelegate;
        }

        private void GenerateObjectType(TypeBuilder type)
        {
            var objectType = type.DefineProperty("ObjectType", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
            var get = type.DefineMethod("get_ObjectType",
                                        MethodAttributes.Public |
                                            MethodAttributes.HideBySig |
                                            MethodAttributes.SpecialName |
                                            MethodAttributes.Virtual,
                                        typeof(string),
                                        Type.EmptyTypes);
            var emit = new ILEmitter(get, new[] { type });
            emit.LdStr(type.FullName)
                .Ret();

            objectType.SetGetMethod(get);
        }

        private void GenerateConstructor(ObjectInfo info, ConstructorInfo parentConstructor, MethodInfo createScript)
        {
            var ctorMethod = (ConstructorBuilder)info.Constructor;

            bool defaultCtor = true;
            if (parentConstructor != _baseConstructor)
                defaultCtor = false;

            var ctor = new ILEmitter(ctorMethod, new[] { info.Type, typeof(TsObject[]) });
            ctor.LdArg(0);
            if (defaultCtor == false)
                ctor.LdNull();
            ctor.CallBase(parentConstructor, defaultCtor ? 0 : 1);
            if (createScript != null)
            {
                var notValid = ctor.DefineLabel();
                ctor.LdArg(1)
                    .LdNull()
                    .BeqS(notValid)
                    .LdArg(0)
                    .LdArg(1)
                    .Call(createScript, 2, typeof(TsObject))
                    .Pop()
                    .MarkLabel(notValid);
            }
            ctor.Ret();

            GenerateCreateMethod(info);
        }

        private void GenerateCreateMethod(ObjectInfo info)
        {
            var builder = (TypeBuilder)info.Type;
            var create = builder.DefineMethod("Create",
                                        MethodAttributes.Assembly |
                                            MethodAttributes.Static |
                                            MethodAttributes.HideBySig,
                                        typeof(ITsInstance),
                                        new[] { typeof(TsObject[]) });
            var emit = new ILEmitter(create, new[] { typeof(TsObject[]) });
            emit.LdArg(0)
                .New(info.Constructor, 1)
                .Ret();

            info.Create = create;
        }

        private void GenerateToStringMethod(TypeBuilder type, MethodInfo toString)
        {
            var methodBuilder = type.DefineMethod("ToString",
                                                  MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                                                  CallingConventions.HasThis,
                                                  typeof(string),
                                                  Type.EmptyTypes);
            var emit = new ILEmitter(methodBuilder, Type.EmptyTypes);
            emit.LdArg(0)
                .Call(toString)
                .Call(TsTypes.ObjectCasts[typeof(string)])
                .Ret();
        }

        private void GenerateTryGetDelegate(ObjectInfo info, List<MethodInfo> scripts)
        {
            var mthd = new ILEmitter((MethodBuilder)info.TryGetDelegate, new[] { info.Type, typeof(string), typeof(TsDelegate).MakeByRefType() });

            if (scripts.Count == 0)
            {
                TryGetDelegateDefaultCase(mthd, info.Parent?.TryGetDelegate, info.Members);
                return;
            }

            // Originally tried to use built-in hash function first,
            // but the only feasible way for that to work would be
            // to force a 32bit architecture, because the builtin hash
            // is platform dependant.

            // If more hash functions are needed, they can always be implemented.
            // For now, just use the fnv-1a algorithm. That's what .NET uses under the hood
            // when it switches on a string.

            // The implementation can be found in TaffyScript.Strings in the TaffyScript assembly.

            var hashCodes = new HashSet<int>();
            var bruteForce = new List<MethodInfo>();
            var tree = new RedBlackTree<int, MethodInfo>();
            var hashMethod = typeof(Fnv).GetMethod("Fnv32");

            foreach (var script in scripts)
            {
                var hash = Fnv.Fnv32(script.Name);
                if (!hashCodes.Add(Fnv.Fnv32(script.Name)))
                    bruteForce.Add(script);
                else
                    tree.Insert(hash, script);
            }

            foreach (var method in bruteForce)
            {
                var next = mthd.DefineLabel();
                mthd.LdStr(method.Name)
                    .LdArg(1)
                    .Call(typeof(string).GetMethod("op_Equality"))
                    .BrFalse(next)
                    .LdArg(2)
                    .LdArg(0)
                    .LdFtn(method)
                    .New(typeof(TsScript).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))
                    .LdStr(method.Name)
                    .New(typeof(TsDelegate).GetConstructor(new[] { typeof(TsScript), typeof(string) }))
                    .StIndRef()
                    .LdBool(true)
                    .Ret()
                    .MarkLabel(next);
            }

            var hashVar = mthd.DeclareLocal(typeof(int), "hashLocal");

            mthd.LdArg(1)
                .Call(hashMethod)
                .StLocal(hashVar);

            var end = mthd.DefineLabel();

            TryGetDelegateSwitchNode(tree.Root, mthd, hashVar, end);

            mthd.MarkLabel(end);

            TryGetDelegateDefaultCase(mthd, info.Parent?.TryGetDelegate, info.Members);
        }

        private void TryGetDelegateSwitchNode(RedBlackTree<int, MethodInfo>.RedBlackNode node, ILEmitter mthd, LocalBuilder hashVar, Label end)
        {
            var greaterEqual = mthd.DefineLabel();
            var equal = mthd.DefineLabel();
            mthd.LdLocal(hashVar)
                .LdInt(node.Key)
                .Bge(greaterEqual);

            if (node.Left != RedBlackTree<int, MethodInfo>.Leaf)
                TryGetDelegateSwitchNode(node.Left, mthd, hashVar, end);
            else
                mthd.Br(end);

            mthd.MarkLabel(greaterEqual)
                .LdLocal(hashVar)
                .LdInt(node.Key)
                .Beq(equal);

            if (node.Right != RedBlackTree<int, MethodInfo>.Leaf)
                TryGetDelegateSwitchNode(node.Right, mthd, hashVar, end);
            else
                mthd.Br(end);

            mthd.MarkLabel(equal)
                .LdArg(1)
                .LdStr(node.Value.Name)
                .Call(typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) }))
                .BrFalse(end)
                .LdArg(2)
                .LdArg(0)
                .LdFtn(node.Value)
                .New(typeof(TsScript).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))
                .LdStr(node.Value.Name)
                .New(typeof(TsDelegate).GetConstructor(new[] { typeof(TsScript), typeof(string) }))
                .StIndRef()
                .LdBool(true)
                .Ret();
        }

        private void TryGetDelegateDefaultCase(ILEmitter mthd, MethodInfo parentMethod, FieldInfo members)
        {
            if(parentMethod != null)
            {
                mthd.LdArg(0)
                    .LdArg(1)
                    .LdArg(2)
                    .CallE(parentMethod, 3, typeof(bool))
                    .Ret();
            }
            else
            {
                var member = mthd.DeclareLocal(typeof(TsObject), "member");
                var memberLookupFailed = mthd.DefineLabel();
                var memberRightType = mthd.DefineLabel();

                mthd.LdArg(0)
                    .LdFld(members)
                    .LdArg(1)
                    .LdLocalA(member)
                    .Call(typeof(Dictionary<string, TsObject>).GetMethod("TryGetValue"))
                    .BrFalseS(memberLookupFailed)
                    .LdLocal(member)
                    .Call(typeof(TsObject).GetMethod("get_Type"))
                    .LdInt((int)VariableType.Delegate)
                    .BeqS(memberRightType)
                    .LdArg(2)
                    .LdNull()
                    .StIndRef()
                    .LdBool(false)
                    .Ret()
                    .MarkLabel(memberRightType)
                    .LdArg(2)
                    .LdLocal(member)
                    .Call(TsTypes.ObjectCasts[typeof(TsDelegate)])
                    .StIndRef()
                    .LdBool(true)
                    .Ret()
                    .MarkLabel(memberLookupFailed)
                    .LdArg(2)
                    .LdNull()
                    .StIndRef()
                    .LdBool(false)
                    .Ret();
            }
        }

        #endregion
    }

    public class ObjectInfo
    {
        public ObjectInfo Parent { get; }
        public Type Type { get; }
        public FieldInfo Members { get; set; }
        public MethodInfo TryGetDelegate { get; set; }
        public ConstructorInfo Constructor { get; set; }
        public MethodInfo Create { get; set; }
        public Dictionary<string, MethodInfo> Methods { get; } = new Dictionary<string, MethodInfo>();

        public ObjectInfo(Type type, ObjectInfo parent, MethodInfo tryGetDelegate, ConstructorInfo constructor, FieldInfo members)
        {
            Type = type;
            Parent = parent;
            TryGetDelegate = tryGetDelegate;
            Constructor = constructor;
            Members = members;
        }
    }
}
