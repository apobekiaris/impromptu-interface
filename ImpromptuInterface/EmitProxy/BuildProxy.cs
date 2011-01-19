﻿// 
//  Copyright 2010  Ekon Benefits
// 
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
// 
//        http://www.apache.org/licenses/LICENSE-2.0
// 
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.

namespace ImpromptuInterface
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Runtime.CompilerServices;
    using Microsoft.CSharp.RuntimeBinder;
  

    public static class BuildProxy
    {
       

        private static ModuleBuilder _builder;
        internal static ModuleBuilder _tempBuilder;
        internal static AssemblyBuilder _tempSaveAssembly;

        private static AssemblyBuilder _ab;
        private static readonly Dictionary<TypeHash, Type> _typeHash = new Dictionary<TypeHash, Type>();
        private static readonly object TypeCacheLock = new object();

     

        internal class TempBuilder : IDisposable
        {
            private string _name;
            internal TempBuilder(string name)
            {
                _name = name;
            }

            public void Dispose()
            {
                _tempSaveAssembly.Save(string.Format("{0}.dll", _name));
                _tempSaveAssembly = null;
                _tempBuilder = null;
            }
        }

        public static IDisposable WriteOutDll(string name)
        {
             GenerateAssembly(name, AssemblyBuilderAccess.RunAndSave,ref _tempSaveAssembly,ref  _tempBuilder);
            return new TempBuilder(name);
        }


        public static Type BuildType(Type contextType, Type mainInterface, params Type[] otherInterfaces)
        {
            lock (TypeCacheLock)
            {
                var tNewHash = new TypeHash(contextType, new[]{mainInterface}.Concat(otherInterfaces).ToArray());

                if (!_typeHash.ContainsKey(tNewHash))
                {
                    _typeHash[tNewHash] = BuildTypeHelper(Builder,contextType,new[]{mainInterface}.Concat(otherInterfaces).ToArray());
                }

                return _typeHash[tNewHash];
            }

        }

        private static Type BuildTypeHelper(ModuleBuilder builder,Type contextType,params Type[] interfaces)
        {


            var tB = builder.DefineType(
                string.Format("ActLike_{0}_{1}", interfaces.First().Name, Guid.NewGuid().ToString("N")), TypeAttributes.Public | TypeAttributes.Class,
                typeof(ActLikeProxy), interfaces);

            var tC = tB.DefineConstructor(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig, CallingConventions.HasThis, new[] { typeof(object), typeof(Type[]) });
            tC.DefineParameter(1, ParameterAttributes.None, "original");
            tC.DefineParameter(2, ParameterAttributes.None, "interfaces");
            var tConstInfo = typeof(ActLikeProxy).GetConstructor(BindingFlags.NonPublic |BindingFlags.Instance, null,new[] { typeof(object), typeof(Type[])},null);

            var tCIl = tC.GetILGenerator();
            tCIl.Emit(OpCodes.Ldarg_0);
            tCIl.Emit(OpCodes.Ldarg_1);
            tCIl.Emit(OpCodes.Ldarg_2);
            tCIl.Emit(OpCodes.Call, tConstInfo);
            tCIl.Emit(OpCodes.Ret);

            var tInterfaces = interfaces.Concat(interfaces.SelectMany(it => it.GetInterfaces()));

            foreach (var tInterface in tInterfaces)
            {
                foreach (var tInfo in tInterface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    MakeProperty(builder,tInfo, tB, contextType);
                }
                foreach (var tInfo in tInterface.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(it => !it.IsSpecialName))
                {
                    MakeMethod(builder,tInfo, tB, contextType);
                }
            }
            var tType = tB.CreateType();
            return tType;
        }

        private static IEnumerable<Type> FlattenGenericParameters(Type type)
        {
            if (type.IsGenericParameter)
                return new[] {type};
            if(type.ContainsGenericParameters)
            {
                return type.GetGenericArguments().SelectMany(FlattenGenericParameters);
            }
            return new Type[]{};
        }

        private static Type ReplaceTypeWithGenericBuilder(Type type, IDictionary<Type,GenericTypeParameterBuilder> dict)
        {
           
            if(type.IsGenericTypeDefinition)
            {
                var tArgs = type.GetGenericArguments().Select(it=>ReplaceTypeWithGenericBuilder(it,dict));

                var tNewType = type.MakeGenericType(tArgs.ToArray());
                return tNewType;
            }
            if(dict.ContainsKey(type))
            {
                var tNewType = dict[type];
                var tAttributes = type.GenericParameterAttributes;
                tNewType.SetGenericParameterAttributes(tAttributes);
                foreach (var tConstraint in type.GetGenericParameterConstraints())
                {
                    if(tConstraint.IsInterface)
                        tNewType.SetInterfaceConstraints(tConstraint);
                    else
                        tNewType.SetBaseTypeConstraint(tConstraint);
                }
                return tNewType;
            }
            return type;
        }

        private static void MakeMethod(ModuleBuilder builder,MethodInfo info, TypeBuilder typeBuilder, Type contextType)
        {
            var tName = info.Name;

            Type[] tParamTypes = info.GetParameters().Select(it => it.ParameterType).ToArray();

            var tReturnType = info.ReturnParameter.ParameterType;


            var tCallSiteInvokeName = string.Format("Impromptu_Callsite_{1}_{0}", Guid.NewGuid().ToString("N"), tName);
            var tCStp = DefineBuilderForCallSite(builder, tCallSiteInvokeName);

           

            var tReplacedTypes = GetParamTypes(tCStp, info);
            if (tReplacedTypes != null)
            {
                tReturnType = tReplacedTypes.Item1;
                tParamTypes = tReplacedTypes.Item2;
            }

            var tConvert = "Convert_Method";
            Type tConvertFuncType = null;
            if (tReturnType != typeof(void))
            {
                tConvertFuncType = tCStp.DefineCallsiteField(tConvert, tReturnType);
            }

            var tInvokeMethod = "Invoke_Method";
            var tInvokeFuncType = tCStp.DefineCallsiteField(tInvokeMethod, tReturnType != typeof(void) ? typeof(object) : typeof(void), tParamTypes);




            var tCallSite = tCStp.CreateType();


            var tMethodBuilder = typeBuilder.DefineMethod(tName,
                                                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot);




            tReplacedTypes = GetParamTypes(tMethodBuilder, info);
            if (tReplacedTypes != null)
            {
                tReturnType = tReplacedTypes.Item1;
                tParamTypes = tReplacedTypes.Item2;

                tCallSite = tCallSite.GetGenericTypeDefinition().MakeGenericType(tParamTypes);
                if(tConvertFuncType !=null)
                    tConvertFuncType = UpdateCallsiteFuncType(tConvertFuncType, tReturnType);
                tInvokeFuncType = UpdateCallsiteFuncType(tInvokeFuncType, tReturnType != typeof(void) ? typeof(object) : typeof(void), tParamTypes);
            }

            tMethodBuilder.SetReturnType(tReturnType);
            tMethodBuilder.SetParameters(tParamTypes);

            foreach (var tParam in info.GetParameters())
            {
                tMethodBuilder.DefineParameter(tParam.Position + 1, AttributesForParam(tParam), tParam.Name);
            }

            EmitMethodBody(tName, tParamTypes, tReturnType, tConvert, tInvokeMethod, tMethodBuilder, tCallSite, contextType, tConvertFuncType, tInvokeFuncType);
        }

        private static TypeBuilder DefineBuilderForCallSite(ModuleBuilder builder, string tCallSiteInvokeName)
        {
            return builder.DefineType(tCallSiteInvokeName,

                                      TypeAttributes.NotPublic
                                      | TypeAttributes.Sealed
                                      | TypeAttributes.AutoClass
                                      |TypeAttributes.BeforeFieldInit 
                                      | TypeAttributes.Abstract
                );
        }

        private static Tuple<Type, Type[]> GetParamTypes(dynamic builder, MethodInfo info)
        {
            var paramTypes = info.GetParameters().Select(it => it.ParameterType).ToArray();
            var returnType = info.ReturnParameter.ParameterType;

            var tGenericParams = paramTypes
                .SelectMany(FlattenGenericParameters)
                .Distinct().ToDictionary(it => it.GenericParameterPosition, it => new { Type = it, Gen = default(GenericTypeParameterBuilder) });
            var tParams = tGenericParams;
            var tReturnParameters = FlattenGenericParameters(returnType).Where(it => !tParams.ContainsKey(it.GenericParameterPosition));
            foreach(var tReParm in tReturnParameters)
                tGenericParams.Add(tReParm.GenericParameterPosition, new { Type = tReParm, Gen = default(GenericTypeParameterBuilder) });
            var tGenParams = tGenericParams.OrderBy(it => it.Key).Select(it => it.Value.Type.Name);
            if (tGenParams.Any())
            {
                GenericTypeParameterBuilder[] tBuilders = builder.DefineGenericParameters(tGenParams.ToArray());
                var tDict = tGenericParams.ToDictionary(param => param.Value.Type, param => tBuilders[param.Key]);

                returnType = ReplaceTypeWithGenericBuilder(returnType, tDict);
                if (tDict.ContainsKey(returnType))
                {
                    returnType = tDict[returnType];
                }
                paramTypes = paramTypes.Select(it => ReplaceTypeWithGenericBuilder(it,tDict)).ToArray();
                return Tuple.Create(returnType, paramTypes);
            }
            return null;
        }

        private static void EmitMethodBody(
            string name,
            Type[] paramTypes, 
            Type returnType, 
            string convert,
            string invokeMethod, 
            MethodBuilder methodBuilder, 
            Type callSite,
            Type contextType, 
            Type convertFuncType, 
            Type invokeFuncType)
        {
            var tIlGen = methodBuilder.GetILGenerator();

            var tConvertField = callSite.GetFieldEvenIfGeneric(convert);
            if (returnType != typeof(void))
            {

                tIlGen.Emit(OpCodes.Ldsfld, tConvertField);
                using (tIlGen.EmitBranchTrue())
                {
                    tIlGen.EmitDynamicConvertBinder(CSharpBinderFlags.None, returnType, contextType);
                    tIlGen.EmitCallsiteCreate(convertFuncType);
                    tIlGen.Emit(OpCodes.Stsfld, tConvertField);
                }
            }
            
            var tInvokeField = callSite.GetFieldEvenIfGeneric(invokeMethod);

            tIlGen.Emit(OpCodes.Ldsfld, tInvokeField);
            using (tIlGen.EmitBranchTrue())
            {
                tIlGen.EmitDynamicMethodInvokeBinder(returnType == typeof(void) ? CSharpBinderFlags.ResultDiscarded : CSharpBinderFlags.None, name, contextType, paramTypes);
                tIlGen.EmitCallsiteCreate(invokeFuncType);
                tIlGen.Emit(OpCodes.Stsfld, tInvokeField);
            }

            if (returnType != typeof(void))
            {
                tIlGen.Emit(OpCodes.Ldsfld, tConvertField);
                tIlGen.Emit(OpCodes.Ldfld, typeof(CallSite<>).MakeGenericType(convertFuncType).GetFieldEvenIfGeneric("Target"));
                tIlGen.Emit(OpCodes.Ldsfld, tConvertField);
            }

            tIlGen.Emit(OpCodes.Ldsfld, tInvokeField);
            tIlGen.Emit(OpCodes.Ldfld, typeof(CallSite<>).MakeGenericType(invokeFuncType).GetFieldEvenIfGeneric("Target"));
            tIlGen.Emit(OpCodes.Ldsfld, tInvokeField);
            tIlGen.Emit(OpCodes.Ldarg_0);
            tIlGen.Emit(OpCodes.Call, typeof(ActLikeProxy).GetProperty("Original").GetGetMethod());
            for (var i = 1; i <= paramTypes.Length; i++)
            {

                tIlGen.EmitLoadArgument(i);
            }
            tIlGen.EmitCallInvokeFunc(invokeFuncType, returnType == typeof(void));
            if (returnType != typeof(void))
            {
                tIlGen.EmitCallInvokeFunc(convertFuncType);
            }

            tIlGen.Emit(OpCodes.Ret);
        }


        private static void MakeProperty(ModuleBuilder builder,PropertyInfo info, TypeBuilder typeBuilder, Type contextType)
        {
            var tName = info.Name;

            var tGetMethod = info.GetGetMethod();
            var tSetMethod = info.GetSetMethod();

            Type[] tSetParamTypes = null;
            Type tInvokeSetFuncType = null;

            var tIndexParamTypes = info.GetIndexParameters().Select(it => it.ParameterType).ToArray();




            var tCallSiteInvokeName = string.Format("Impromptu_Callsite_{1}_{0}", Guid.NewGuid().ToString("N"), tName);
            var tCStp = DefineBuilderForCallSite(builder, tCallSiteInvokeName);


            var tConvertGet = "Convert_Get";
            var tConvertFuncType = tCStp.DefineCallsiteField(tConvertGet, tGetMethod.ReturnType);

            var tInvokeGet = "Invoke_Get";
            var tInvokeGetFuncType = tCStp.DefineCallsiteField(tInvokeGet, typeof(object), tIndexParamTypes);
            
            var tInvokeSet = "Invoke_Set";
            if (tSetMethod != null)
            {
                tSetParamTypes = tSetMethod.GetParameters().Select(it => it.ParameterType).ToArray();
                
                tInvokeSetFuncType = tCStp.DefineCallsiteField(tInvokeSet, typeof (object), tSetParamTypes);
            }

            var tCallSite = tCStp.CreateType();

            var tMp = typeBuilder.DefineProperty(tName, PropertyAttributes.None, tGetMethod.ReturnType, tIndexParamTypes);



            //GetMethod
            var tGetMethodBuilder = typeBuilder.DefineMethod(tGetMethod.Name,
                                                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                                                    tGetMethod.ReturnType,
                                                    tIndexParamTypes);


          

            foreach (var tParam in info.GetGetMethod().GetParameters())
            {


                tGetMethodBuilder.DefineParameter(tParam.Position + 1, AttributesForParam(tParam), tParam.Name);
            }

            EmitProperty(
                info,
                tName, 
                tConvertGet,
                tGetMethod, 
                tInvokeGet, 
                tIndexParamTypes,
                tSetMethod, 
                tInvokeSet, 
                tSetParamTypes, 
                typeBuilder,
                tGetMethodBuilder,
                tCallSite, 
                contextType,
                tConvertFuncType, 
                tInvokeGetFuncType, 
                tMp, 
                tInvokeSetFuncType);
        }

        private static void EmitProperty(
            PropertyInfo info, 
            string name, 
            string convertGet,
            MethodInfo getMethod, 
            string invokeGet, 
            Type[] indexParamTypes,
            MethodInfo setMethod,
            string invokeSet,
            Type[] setParamTypes, 
            TypeBuilder typeBuilder,
            MethodBuilder getMethodBuilder, 
            Type callSite,
            Type contextType, 
            Type tConvertFuncType, 
            Type invokeGetFuncType, 
            PropertyBuilder tMp, 
            Type invokeSetFuncType)
        {
            if (getMethod == null) throw new ArgumentNullException("getMethod");
            if (indexParamTypes == null) throw new ArgumentNullException("indexParamTypes");
            var tIlGen = getMethodBuilder.GetILGenerator();

            var tConvertCallsiteField = callSite.GetFieldEvenIfGeneric(convertGet);
            var tReturnLocal =tIlGen.DeclareLocal(getMethod.ReturnType);


       
            tIlGen.Emit(OpCodes.Ldsfld, tConvertCallsiteField);
            using (tIlGen.EmitBranchTrue())
            {
                tIlGen.EmitDynamicConvertBinder(CSharpBinderFlags.None, getMethod.ReturnType, contextType);
                tIlGen.EmitCallsiteCreate(tConvertFuncType);
                tIlGen.Emit(OpCodes.Stsfld, tConvertCallsiteField);
            }

            var tInvokeGetCallsiteField = callSite.GetFieldEvenIfGeneric(invokeGet);
            tIlGen.Emit(OpCodes.Ldsfld, tInvokeGetCallsiteField);
            using (tIlGen.EmitBranchTrue())
            {
                tIlGen.EmitDynamicGetBinder(CSharpBinderFlags.None, name, contextType, indexParamTypes);
                tIlGen.EmitCallsiteCreate(invokeGetFuncType);
                tIlGen.Emit(OpCodes.Stsfld, tInvokeGetCallsiteField);
            }


            tIlGen.Emit(OpCodes.Ldsfld, tConvertCallsiteField);
            tIlGen.Emit(OpCodes.Ldfld, tConvertCallsiteField.FieldType.GetFieldEvenIfGeneric("Target"));
            tIlGen.Emit(OpCodes.Ldsfld, tConvertCallsiteField);
            tIlGen.Emit(OpCodes.Ldsfld, tInvokeGetCallsiteField);
            tIlGen.Emit(OpCodes.Ldfld, tInvokeGetCallsiteField.FieldType.GetFieldEvenIfGeneric("Target"));
            tIlGen.Emit(OpCodes.Ldsfld, tInvokeGetCallsiteField);
            tIlGen.Emit(OpCodes.Ldarg_0);
            tIlGen.Emit(OpCodes.Call, typeof(ActLikeProxy).GetProperty("Original").GetGetMethod());
            for (var i = 1; i <= indexParamTypes.Length; i++)
            {
                tIlGen.EmitLoadArgument(i);
            }
            tIlGen.EmitCallInvokeFunc(invokeGetFuncType);
            tIlGen.EmitCallInvokeFunc(tConvertFuncType);
            tIlGen.EmitStoreLocation(tReturnLocal.LocalIndex);
            var tReturnLabel =tIlGen.DefineLabel();
            tIlGen.Emit(OpCodes.Br_S, tReturnLabel);
            tIlGen.MarkLabel(tReturnLabel);
            tIlGen.EmitLoadLocation(tReturnLocal.LocalIndex);
            tIlGen.Emit(OpCodes.Ret);
            tMp.SetGetMethod(getMethodBuilder);

            if (setMethod != null)
            {
                var tSetMethodBuilder = typeBuilder.DefineMethod(setMethod.Name,
                                                                 MethodAttributes.Public | MethodAttributes.SpecialName |
                                                                 MethodAttributes.HideBySig | MethodAttributes.Virtual |
                                                                 MethodAttributes.Final | MethodAttributes.NewSlot,
                                                                 null,
                                                                 setParamTypes);

                foreach (var tParam in info.GetSetMethod().GetParameters())
                {
                    tSetMethodBuilder.DefineParameter(tParam.Position + 1, AttributesForParam(tParam), tParam.Name);
                }

                tIlGen = tSetMethodBuilder.GetILGenerator();
                var tSetCallsiteField = callSite.GetFieldEvenIfGeneric(invokeSet);
                tIlGen.Emit(OpCodes.Ldsfld, tSetCallsiteField);
                using (tIlGen.EmitBranchTrue())
                {
                    tIlGen.EmitDynamicSetBinder(CSharpBinderFlags.None, name, contextType, setParamTypes);
                    tIlGen.EmitCallsiteCreate(invokeSetFuncType);
                    tIlGen.Emit(OpCodes.Stsfld, tSetCallsiteField);
                }
                tIlGen.Emit(OpCodes.Ldsfld, tSetCallsiteField);
                tIlGen.Emit(OpCodes.Ldfld, tSetCallsiteField.FieldType.GetFieldEvenIfGeneric("Target"));
                tIlGen.Emit(OpCodes.Ldsfld, tSetCallsiteField);
                tIlGen.Emit(OpCodes.Ldarg_0);
                tIlGen.Emit(OpCodes.Call, typeof (ActLikeProxy).GetProperty("Original").GetGetMethod());
                for (var i = 1; i <= setParamTypes.Length; i++)
                {
                    tIlGen.EmitLoadArgument(i);
                }
                tIlGen.EmitCallInvokeFunc(invokeSetFuncType);
                tIlGen.Emit(OpCodes.Pop);
                tIlGen.Emit(OpCodes.Ret);
                tMp.SetSetMethod(tSetMethodBuilder);
            }
        }


        private static Type UpdateCallsiteFuncType(Type tFuncGeneric, Type returnType, params Type[] argTypes)
        {
            var tList = new List<Type> { typeof(CallSite), typeof(object) };
            tList.AddRange(argTypes);
            if (returnType != typeof(void))
                tList.Add(returnType);

            var tFuncType = tFuncGeneric.GetGenericTypeDefinition().MakeGenericType(tList.ToArray());

            return tFuncType;
        }

        private static Type DefineCallsiteField(this TypeBuilder builder, string name, Type returnType, params Type[] argTypes)
        {
            var tList = new List<Type> { typeof(CallSite), typeof(object) };
            tList.AddRange(argTypes);
            if (returnType != typeof(void))
                tList.Add(returnType);




            var tTypeName = String.Format("System.Func`{0}", tList.Count);
            if (returnType == typeof(void))
                tTypeName = String.Format("System.Action`{0}", tList.Count);



            var tFuncGeneric = Type.GetType(tTypeName);

                var tFuncType = tFuncGeneric.MakeGenericType(tList.ToArray());
                var tReturnType = typeof(CallSite<>).MakeGenericType(tFuncType);
                builder.DefineField(name, tReturnType, FieldAttributes.Static | FieldAttributes.Public);
                return tFuncType;
            
        }

        private static ParameterAttributes AttributesForParam(ParameterInfo param)
        {
            var tAttribute = ParameterAttributes.None;
            if (param.IsIn)
                tAttribute |= ParameterAttributes.In;
            if (param.IsOut)
                tAttribute |= ParameterAttributes.Out;
            if (param.IsOptional)
                tAttribute |= ParameterAttributes.Optional;
            if (param.IsLcid)
                tAttribute |= ParameterAttributes.Lcid;
            return tAttribute;
        }

     
        static public ModuleBuilder Builder
        {
            get
            {
                if (_builder == null)
                {

                    var access = AssemblyBuilderAccess.Run;
                    var tPlainName = "ImpromptuInterfaceDynamicAssembly";


                    GenerateAssembly(tPlainName, access, ref _ab,ref _builder);
                }
                return _tempBuilder ?? _builder;
            }
        }

        private static void GenerateAssembly(string name, AssemblyBuilderAccess access, ref AssemblyBuilder ab, ref ModuleBuilder mb )
        {
            var tName = new AssemblyName(name);

            ab =
                AppDomain.CurrentDomain.DefineDynamicAssembly(
                    tName,
                    access);


            if (access== AssemblyBuilderAccess.RunAndSave || access == AssemblyBuilderAccess.Save)
                mb = ab.DefineDynamicModule("MainModule", string.Format("{0}.dll", tName.Name));
            else
                mb = ab.DefineDynamicModule("MainModule");
        }
    }
    
}