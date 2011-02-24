﻿//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.Cci;
using Microsoft.Cci.MetadataReader;
using Microsoft.Cci.MutableCodeModel;
using Microsoft.Cci.Contracts;
using Microsoft.Cci.ILToCodeModel;

using Bpl = Microsoft.Boogie;
using System.Diagnostics.Contracts;


namespace BytecodeTranslator {

  /// <summary>
  /// Responsible for traversing all metadata elements (i.e., everything exclusive
  /// of method bodies).
  /// </summary>
  public class MetadataTraverser : BaseMetadataTraverser {

    readonly Sink sink;

    public readonly TraverserFactory factory;

    public readonly PdbReader/*?*/ PdbReader;

    public Bpl.Variable HeapVariable;


    public MetadataTraverser(Sink sink, PdbReader/*?*/ pdbReader)
      : base() {
      this.sink = sink;
      this.factory = sink.Factory;
      this.PdbReader = pdbReader;
    }

    public Bpl.Program TranslatedProgram {
      get { return this.sink.TranslatedProgram; }
    }

    #region Overrides

    public override void Visit(IModule module) {
      base.Visit(module);
    }

    public override void Visit(IAssembly assembly) {
      base.Visit(assembly);
      foreach (ITypeDefinition type in sink.delegateTypeToDelegates.Keys)
      {
        CreateDispatchMethod(type);
      }
    }

    private void CreateDispatchMethod(ITypeDefinition type)
    {
      Contract.Assert(type.IsDelegate);
      IMethodDefinition method = null;
      foreach (IMethodDefinition m in type.Methods)
      {
        if (m.Name.Value == "Invoke")
        {
          method = m;
          break;
        }
      }

      Dictionary<IParameterDefinition, MethodParameter> formalMap = new Dictionary<IParameterDefinition, MethodParameter>();
      this.sink.BeginMethod();

      try
      {
        #region Create in- and out-parameters

        int in_count = 0;
        int out_count = 0;
        MethodParameter mp;
        foreach (IParameterDefinition formal in method.Parameters)
        {
          mp = new MethodParameter(formal);
          if (mp.inParameterCopy != null) in_count++;
          if (mp.outParameterCopy != null && (formal.IsByReference || formal.IsOut))
            out_count++;
          formalMap.Add(formal, mp);
        }
        this.sink.FormalMap = formalMap;

        #region Look for Returnvalue
        if (method.Type.TypeCode != PrimitiveTypeCode.Void)
        {
          Bpl.Type rettype = TranslationHelper.CciTypeToBoogie(method.Type);
          out_count++;
          this.sink.RetVariable = new Bpl.Formal(method.Token(),
              new Bpl.TypedIdent(method.Token(),
                  "$result", rettype), false);
        }
        else
        {
          this.sink.RetVariable = null;
        }

        #endregion

        in_count++; // for the function pointer parameter

        Bpl.Variable[] invars = new Bpl.Formal[in_count];
        Bpl.Variable[] outvars = new Bpl.Formal[out_count];

        int i = 0;
        int j = 0;

        // Create function pointer parameter
        invars[i++] = new Bpl.Formal(method.Token(), new Bpl.TypedIdent(method.Token(), "this", Bpl.Type.Int), true);

        foreach (MethodParameter mparam in formalMap.Values)
        {
          if (mparam.inParameterCopy != null)
          {
            invars[i++] = mparam.inParameterCopy;
          }
          if (mparam.outParameterCopy != null)
          {
            if (mparam.underlyingParameter.IsByReference || mparam.underlyingParameter.IsOut)
              outvars[j++] = mparam.outParameterCopy;
          }
        }

        #region add the returnvalue to out if there is one
        if (this.sink.RetVariable != null) outvars[j] = this.sink.RetVariable;
        #endregion

        #endregion

        string MethodName = TranslationHelper.CreateUniqueMethodName(method);

        Bpl.Procedure proc = new Bpl.Procedure(method.Token(),
            MethodName, // make it unique!
            new Bpl.TypeVariableSeq(),
            new Bpl.VariableSeq(invars), // in
            new Bpl.VariableSeq(outvars), // out
            new Bpl.RequiresSeq(),
            new Bpl.IdentifierExprSeq(),
            new Bpl.EnsuresSeq());

        this.sink.TranslatedProgram.TopLevelDeclarations.Add(proc);

        List<Bpl.Block> blocks = new List<Bpl.Block>();
        Bpl.StringSeq labelTargets = new Bpl.StringSeq();
        Bpl.BlockSeq blockTargets = new Bpl.BlockSeq();
        string l = "blocked";
        Bpl.Block b = new Bpl.Block(method.Token(), l, 
          new Bpl.CmdSeq(new Bpl.AssumeCmd(method.Token(), Bpl.Expr.False)), 
          new Bpl.ReturnCmd(method.Token()));
        labelTargets.Add(l);
        blockTargets.Add(b);
        blocks.Add(b);
        foreach (Bpl.Constant c in sink.delegateTypeToDelegates[type])
        {
          Bpl.Expr bexpr = Bpl.Expr.Binary(Bpl.BinaryOperator.Opcode.Eq, Bpl.Expr.Ident(invars[0]), Bpl.Expr.Ident(c));
          Bpl.AssumeCmd assumeCmd = new Bpl.AssumeCmd(method.Token(), bexpr);
          Bpl.ExprSeq ins = new Bpl.ExprSeq();
          Bpl.IdentifierExprSeq outs = new Bpl.IdentifierExprSeq();
          int index;
          for (index = 1; index < invars.Length; index++)
          {
            ins.Add(Bpl.Expr.Ident(invars[index]));
          }
          for (index = 0; index < outvars.Length; index++)
          {
            outs.Add(Bpl.Expr.Ident(outvars[index]));
          }
          Bpl.CallCmd callCmd = new Bpl.CallCmd(method.Token(), c.Name, ins, outs);
          l = "label_" + c.Name;
          b = new Bpl.Block(method.Token(), l, new Bpl.CmdSeq(assumeCmd, callCmd), new Bpl.ReturnCmd(method.Token()));
          labelTargets.Add(l);
          blockTargets.Add(b);
          blocks.Add(b);
        }
        Bpl.GotoCmd gotoCmd = new Bpl.GotoCmd(method.Token(), labelTargets, blockTargets);
        Bpl.Block initialBlock = new Bpl.Block(method.Token(), "start", new Bpl.CmdSeq(), gotoCmd);
        blocks.Insert(0, initialBlock);

        Bpl.Implementation impl =
            new Bpl.Implementation(method.Token(),
                MethodName, // make unique
                new Microsoft.Boogie.TypeVariableSeq(),
                new Microsoft.Boogie.VariableSeq(invars),
                new Microsoft.Boogie.VariableSeq(outvars),
                new Bpl.VariableSeq(),
                blocks
                );

        impl.Proc = proc;
        this.sink.TranslatedProgram.TopLevelDeclarations.Add(impl);
      }
      catch (TranslationException te)
      {
        throw new NotImplementedException(te.ToString());
      }
      catch
      {
        throw;
      }
      finally
      {
        // Maybe this is a good place to add the procedure to the toplevel declarations
      }
    }

    /// <summary>
    /// Visits only classes: throws an exception for all other type definitions.
    /// </summary>
    /// 


    public override void Visit(ITypeDefinition typeDefinition) {

      if (typeDefinition.IsClass) {
        base.Visit(typeDefinition);
      } else if (typeDefinition.IsDelegate) {
        sink.AddDelegateType(typeDefinition);
      } else {
        Console.WriteLine("Non-Class {0} was found", typeDefinition);
        throw new NotImplementedException(String.Format("Non-Class Type {0} is not yet supported.", typeDefinition.ToString()));
      }
    }

    #region Local state for each method

    #endregion

    /// <summary>
    /// 
    /// </summary>
    public override void Visit(IMethodDefinition method) {

      this.sink.BeginMethod();

      var proc = this.sink.FindOrCreateProcedure(method, method.IsStatic);

      try {

        if (method.IsAbstract) {
          throw new NotImplementedException("abstract methods are not yet implemented");
        }

        StatementTraverser stmtTraverser = this.factory.MakeStatementTraverser(this.sink, this.PdbReader);

        #region Add assignements from In-Params to local-Params

        foreach (MethodParameter mparam in this.sink.FormalMap.Values) {
          if (mparam.inParameterCopy != null) {
            Bpl.IToken tok = method.Token();
            stmtTraverser.StmtBuilder.Add(Bpl.Cmd.SimpleAssign(tok,
              new Bpl.IdentifierExpr(tok, mparam.outParameterCopy),
              new Bpl.IdentifierExpr(tok, mparam.inParameterCopy)));
          }
        }

        #endregion

        try {
          method.Body.Dispatch(stmtTraverser);
        } catch (TranslationException te) {
          throw new NotImplementedException("No Errorhandling in Methodvisitor / " + te.ToString());
        } catch {
          throw;
        }

        #region Create Local Vars For Implementation
        List<Bpl.Variable> vars = new List<Bpl.Variable>();
        foreach (MethodParameter mparam in this.sink.FormalMap.Values) {
          if (!(mparam.underlyingParameter.IsByReference || mparam.underlyingParameter.IsOut))
            vars.Add(mparam.outParameterCopy);
        }
        foreach (Bpl.Variable v in this.sink.LocalVarMap.Values) {
          vars.Add(v);
        }

        Bpl.VariableSeq vseq = new Bpl.VariableSeq(vars.ToArray());
        #endregion

        Bpl.Implementation impl =
            new Bpl.Implementation(method.Token(),
                proc.Name,
                new Microsoft.Boogie.TypeVariableSeq(),
                proc.InParams,
                proc.OutParams,
                vseq,
                stmtTraverser.StmtBuilder.Collect(Bpl.Token.NoToken));

        impl.Proc = proc;

        // Don't need an expression translator because there is a limited set of things
        // that can appear as arguments to custom attributes
        foreach (var a in method.Attributes) {
          var attrName = TypeHelper.GetTypeName(a.Type);
          if (attrName.EndsWith("Attribute"))
            attrName = attrName.Substring(0, attrName.Length - 9);
          var args = new object[IteratorHelper.EnumerableCount(a.Arguments)];
          int argIndex = 0;
          foreach (var c in a.Arguments) {
            var mdc = c as IMetadataConstant;
            if (mdc != null) {
              object o;
              switch (mdc.Type.TypeCode) {
                case PrimitiveTypeCode.Boolean:
                  o = (bool)mdc.Value ? Bpl.Expr.True : Bpl.Expr.False;
                  break;
                case PrimitiveTypeCode.Int32:
                  o = Bpl.Expr.Literal((int)mdc.Value);
                  break;
                case PrimitiveTypeCode.String:
                  o = mdc.Value;
                  break;
                default:
                  throw new InvalidCastException("Invalid metadata constant type");
              }
              args[argIndex++] = o;
            }
          }
          impl.AddAttribute(attrName, args);
        }

        this.sink.TranslatedProgram.TopLevelDeclarations.Add(impl);

      } catch (TranslationException te) {
        throw new NotImplementedException(te.ToString());
      } catch {
        throw;
      } finally {
        // Maybe this is a good place to add the procedure to the toplevel declarations
      }
    }

    public override void Visit(IFieldDefinition fieldDefinition) {
      this.sink.FindOrCreateFieldVariable(fieldDefinition);
    }

    #endregion

  }
}