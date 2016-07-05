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
using System.Text.RegularExpressions;
using System.Diagnostics.Contracts;

namespace BytecodeTranslator {

  public class MethodParameter {
    
    /// <summary>
    /// All parameters of the method get an associated in parameter
    /// in the Boogie procedure except for out parameters.
    /// </summary>
    public Bpl.Formal/*?*/ inParameterCopy;
    
    /// <summary>
    /// A local variable when the underlyingParameter is an in parameter
    /// and a formal (out) parameter when the underlyingParameter is
    /// a ref or out parameter.
    /// </summary>
    public Bpl.Variable outParameterCopy;

    public IParameterDefinition underlyingParameter;

    public MethodParameter(IParameterDefinition parameterDefinition, Bpl.Type ptype) {
      this.underlyingParameter = parameterDefinition;

      var parameterToken = parameterDefinition.Token();
      var typeToken = parameterDefinition.Type.Token();
      var parameterName = TranslationHelper.TurnStringIntoValidIdentifier(parameterDefinition.Name.Value);
      if (String.IsNullOrWhiteSpace(parameterName)) parameterName = "P" + parameterDefinition.Index.ToString();

      this.inParameterCopy = new Bpl.Formal(parameterToken, new Bpl.TypedIdent(typeToken, parameterName + "$in", ptype), true);
      if (parameterDefinition.IsByReference) {
        this.outParameterCopy = new Bpl.Formal(parameterToken, new Bpl.TypedIdent(typeToken, parameterName + "$out", ptype), false);
      } else {
        this.outParameterCopy = new Bpl.LocalVariable(parameterToken, new Bpl.TypedIdent(typeToken, parameterName, ptype));
      }
    }

    public override string ToString() {
      return this.underlyingParameter.Name.Value;
    }
  }

    /// <summary>
    /// Class containing several static helper functions to convert
    /// from Cci to Boogie
    /// </summary>
  static class TranslationHelper {
    public static Bpl.StmtList BuildStmtList(Bpl.Cmd cmd, Bpl.TransferCmd tcmd) {
      Bpl.StmtListBuilder builder = new Bpl.StmtListBuilder();
      builder.Add(cmd);
      builder.Add(tcmd);
      return builder.Collect(Bpl.Token.NoToken);
    }

    public static Bpl.StmtList BuildStmtList(Bpl.TransferCmd tcmd) {
      Bpl.StmtListBuilder builder = new Bpl.StmtListBuilder();
      builder.Add(tcmd);
      return builder.Collect(Bpl.Token.NoToken);
    }

    public static Bpl.StmtList BuildStmtList(params Bpl.Cmd[] cmds) {
      Bpl.StmtListBuilder builder = new Bpl.StmtListBuilder();
      foreach (Bpl.Cmd cmd in cmds)
        builder.Add(cmd);
      return builder.Collect(Bpl.Token.NoToken);
    }

    public static Bpl.AssignCmd BuildAssignCmd(Bpl.IdentifierExpr lhs, Bpl.Expr rhs)
    {
      List<Bpl.AssignLhs> lhss = new List<Bpl.AssignLhs>();
      lhss.Add(new Bpl.SimpleAssignLhs(lhs.tok, lhs));
      List<Bpl.Expr> rhss = new List<Bpl.Expr>();
      rhss.Add(rhs);
      return new Bpl.AssignCmd(lhs.tok, lhss, rhss);
    }

    public static Bpl.AssignCmd BuildAssignCmd(List<Bpl.IdentifierExpr> lexprs, List<Bpl.Expr> rexprs) {
      List<Bpl.AssignLhs> lhss = new List<Bpl.AssignLhs>();
      foreach (Bpl.IdentifierExpr lexpr in lexprs) {
        lhss.Add(new Bpl.SimpleAssignLhs(lexpr.tok, lexpr));
      }
      List<Bpl.Expr> rhss = new List<Bpl.Expr>();
      return new Bpl.AssignCmd(Bpl.Token.NoToken, lhss, rexprs);
    }

    public static Bpl.IToken Token(this IObjectWithLocations objectWithLocations) {
      //TODO: use objectWithLocations.Locations!
      Bpl.IToken tok = Bpl.Token.NoToken;
      return tok;
    }

    internal static int tmpVarCounter = 0;
    public static string GenerateTempVarName() {
      return "$tmp" + (tmpVarCounter++).ToString();
    }

    internal static int catchClauseCounter = 0;
    public static string GenerateCatchClauseName() {
      return "catch" + (catchClauseCounter++).ToString();
    }

    internal static int finallyClauseCounter = 0;
    public static string GenerateFinallyClauseName() {
      return "finally" + (finallyClauseCounter++).ToString();
    }

    public static List<IGenericTypeParameter> ConsolidatedGenericParameters(ITypeReference typeReference) {
      Contract.Requires(typeReference != null);

      var typeDefinition = typeReference.ResolvedType;
      var totalParameters = new List<IGenericTypeParameter>();
      ConsolidatedGenericParameters(typeDefinition, totalParameters);
      return totalParameters;

      //var nestedTypeDefinition = typeDefinition as INestedTypeDefinition;
      //while (nestedTypeDefinition != null) {
      //  var containingType = nestedTypeDefinition.ContainingType.ResolvedType;
      //  totalParameters.AddRange(containingType.GenericParameters);
      //  nestedTypeDefinition = containingType as INestedTypeDefinition;
      //}
      //totalParameters.AddRange(typeDefinition.GenericParameters);
      //return totalParameters;
    }
    private static void ConsolidatedGenericParameters(ITypeDefinition typeDefinition, List<IGenericTypeParameter> consolidatedParameters){
      var nestedTypeDefinition = typeDefinition as INestedTypeDefinition;
      if (nestedTypeDefinition != null){
        ConsolidatedGenericParameters(nestedTypeDefinition.ContainingTypeDefinition, consolidatedParameters);
      }
      consolidatedParameters.AddRange(typeDefinition.GenericParameters);
    }

    public static string CreateUniqueMethodName(IMethodReference method) {
      var containingTypeName = TypeHelper.GetTypeName(method.ContainingType, NameFormattingOptions.None);
      var s = MemberHelper.GetMethodSignature(method, NameFormattingOptions.DocumentationId);
      s = s.Substring(2);
      s = s.TrimEnd(')');
      s = TurnStringIntoValidIdentifier(s);
      return s;
    }

    public static string TurnStringIntoValidIdentifier(string s) {

      // Do this specially just to make the resulting string a little bit more readable.
      // REVIEW: Just let the main replacement take care of it?
      s = s.Replace("[0:,0:]", "2DArray"); // TODO: Do this programmatically to handle arbitrary arity
      s = s.Replace("[0:,0:,0:]", "3DArray");
      s = s.Replace("[0:,0:,0:,0:]", "4DArray");
      s = s.Replace("[0:,0:,0:,0:,0:]", "5DArray");
      s = s.Replace("[]", "array");

      // The definition of a Boogie identifier is from BoogiePL.atg.
      // Just negate that to get which characters should be replaced with a dollar sign.

      // letter = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".
      // digit = "0123456789".
      // special = "'~#$^_.?`".
      // nondigit = letter + special.
      // ident =  [ '\\' ] nondigit {nondigit | digit}.

      s = Regex.Replace(s, "[^A-Za-z0-9'~#$^_.?`]", "$");
      
      s = GetRidOfSurrogateCharacters(s);
      return s;
    }

    /// <summary>
    /// Unicode surrogates cannot be handled by Boogie.
    /// http://msdn.microsoft.com/en-us/library/dd374069(v=VS.85).aspx
    /// </summary>
    private static string GetRidOfSurrogateCharacters(string s) {
      //  TODO this is not enough! Actually Boogie cannot support UTF8
      var cs = s.ToCharArray();
      var okayChars = new char[cs.Length];
      for (int i = 0, j = 0; i < cs.Length; i++) {
        if (Char.IsSurrogate(cs[i])) continue;
        okayChars[j++] = cs[i];
      }
      var raw = String.Concat(okayChars);
      return raw.Trim(new char[] { '\0' });
    }

    public static bool IsStruct(ITypeReference typ) {
      return typ.IsValueType && !typ.IsEnum && typ.TypeCode == PrimitiveTypeCode.NotPrimitive;
    }

    // The next two methods are currently intended for record-call labels and
    // handle only the cases that are most important for that purpose.  If they
    // were to be more general, we'd probably need to modify the interface to
    // keep track of precedence information so we could add the required
    // parentheses.

    public static string ExpressionToSource(IExpression expr) {
      var boundExpr = expr as IBoundExpression;
      if (boundExpr != null)
        return BoundOrTargetExpressionToSource(boundExpr.Definition, boundExpr.Instance, false);
      if (expr is IThisReference)
        return "this";
      return "<expr>";
    }

    // Note: As per the ITargetExpression and IBoundExpression contracts,
    // "definition" (confusingly named) is a definition if it represents a local
    // variable or parameter, but otherwise it may be a reference.  (There is no
    // ILocalReference or IParameterReference, probably because locals and
    // parameters are always referenced in the same assembly in which they're
    // defined.)
    public static string BoundOrTargetExpressionToSource(object definition, IExpression/*?*/ instance, bool isTarget) {
      if (definition is ILocalDefinition || definition is IParameterDefinition)
      {
        // XXX In theory, we should escape keywords with "@" if we knew this was C#.
        return ((INamedEntity)definition).Name.Value;
      }

      var/*?*/ field = definition as IFieldReference;
      if (field != null) {
        if (instance == null) {
          return MemberHelper.GetMemberSignature(field);
        } else {
          return ExpressionToSource(instance) + "." + field.Name.Value;
        }
      }

      return (isTarget ? "<lvalue>" : "<expr>");
    }

    public static void AddRecordCall(
      Sink sink, Bpl.StmtListBuilder statementBuilder,
      string label, IExpression value, Bpl.Expr valueBpl) {
      // valueBpl.Type only gets set in a few simple cases, while
      // sink.CciTypeToBoogie(value.Type.ResolvedType) should always be correct
      // if BCT is working properly. *cross fingers*
      // ~ t-mattmc@microsoft.com 2016-06-21
      var logProcedureName = sink.FindOrCreateRecordProcedure(sink.CciTypeToBoogie(value.Type.ResolvedType));
      var call = new Bpl.CallCmd(Bpl.Token.NoToken, logProcedureName, new List<Bpl.Expr> { valueBpl }, new List<Bpl.IdentifierExpr> { });
      // This seems to be the idiom (see Bpl.Program.addUniqueCallAttr).
      // XXX What does the token mean?  Should there be one?
      // ~ t-mattmc@microsoft.com 2016-06-13
      call.Attributes = new Bpl.QKeyValue(Bpl.Token.NoToken, "cexpr", new List<object> { label }, call.Attributes);
      statementBuilder.Add(call);
    }
  }
}
