Option Explicit
Option Strict


' Dynamic Bandwidth Monitor
' Leak detection method implemented in a real-time data historian
' Copyright (C) 2014-2018  J.H. Fitié, Vitens N.V.
'
' This file is part of DBM.
'
' DBM is free software: you can redistribute it and/or modify
' it under the terms of the GNU General Public License as published by
' the Free Software Foundation, either version 3 of the License, or
' (at your option) any later version.
'
' DBM is distributed in the hope that it will be useful,
' but WITHOUT ANY WARRANTY; without even the implied warranty of
' MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
' GNU General Public License for more details.
'
' You should have received a copy of the GNU General Public License
' along with DBM.  If not, see <http://www.gnu.org/licenses/>.


Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.DateTime
Imports System.Math
Imports System.Runtime.InteropServices
Imports System.Threading
Imports OSIsoft.AF.Asset
Imports OSIsoft.AF.Asset.AFAttributeTrait
Imports OSIsoft.AF.Data
Imports OSIsoft.AF.Time
Imports Vitens.DynamicBandwidthMonitor.DBMInfo
Imports Vitens.DynamicBandwidthMonitor.DBMParameters


' Assembly title
<assembly:System.Reflection.AssemblyTitle("DBMDataRef")>


Namespace Vitens.DynamicBandwidthMonitor


  <Description("DBMDataRef;Dynamic Bandwidth Monitor")>
    <Guid("e092c5ed-888b-4dbd-89eb-45206d77db9a")>
    Public Class DBMDataRef
    Inherits AFDataReference


    ' DBMDataRef is a custom OSIsoft PI Asset Framework data reference which
    ' integrates DBM with PI AF. The build script automatically registers the
    ' data reference and support assemblies when run on the PI AF server.
    ' The data reference uses the parent attribute as input and automatically
    ' uses attributes from sibling and parent elements based on the same
    ' template containing good data for correlation calculations, unless the
    ' NoCorrelation category is applied to the output attribute. The value
    ' returned from the DBM calculation is determined by the applied
    ' property/trait:
    '   None      Factor
    '   Target    Measured value
    '   Forecast  Forecast value
    '   Minimum   Lower control limit (p = 0.9999)
    '   LoLo      Lower control limit (default)
    '   Lo        Lower control limit (p = 0.95)
    '   Hi        Upper control limit (p = 0.95)
    '   HiHi      Upper control limit (default)
    '   Maximum   Upper control limit (p = 0.9999)


    Const CategoryNoCorrelation As String = "NoCorrelation"
    Const pValueLoHi As Double = 0.95 ' Confidence interval for Lo and Hi
    Const pValueMinMax As Double = 0.9999 ' CI for Minimum and Maximum


    Private InputPointDriver As DBMPointDriver
    Private CorrelationPoints As List(Of DBMCorrelationPoint)
    Private PointsStale As New DBMStale
    Private Shared DBM As New DBM


    Public Overrides Readonly Property SupportedContexts _
      As AFDataReferenceContext

      Get
        Return AFDataReferenceContext.Time
      End Get

    End Property


    Public Overrides Readonly Property SupportedDataMethods As AFDataMethods

      Get
        Return AFDataMethods.RecordedValue Or AFDataMethods.RecordedValues Or
          AFDataMethods.PlotValues Or AFDataMethods.Summary Or
          AFDataMethods.Summaries
      End Get

    End Property


    Public Overrides Readonly Property SupportedMethods As AFDataReferenceMethod

      Get
        Return AFDataReferenceMethod.GetValue Or AFDataReferenceMethod.GetValues
      End Get

    End Property


    Public Overrides Property ConfigString As String

      Get
        Return LicenseNotice
      End Get

      Set
      End Set

    End Property


    Private Sub UpdatePoints

      ' Retrieve input and correlation PI points from AF hierarchy if owned by
      ' an attribute (element is an instance of an element template) and
      ' attribute has a parent attribute.

      Dim Element, ParentElement, SiblingElement As AFElement

      If Attribute IsNot Nothing And Attribute.Parent IsNot Nothing Then

        Element = DirectCast(Attribute.Element, AFElement)
        InputPointDriver = New DBMPointDriver(Attribute.Parent) ' Parent attrib.
        CorrelationPoints = New List(Of DBMCorrelationPoint)

        ' Retrieve correlation points for non-root elements only when
        ' calculating the DBM factor value and if correlation calculations are
        ' not disabled using categories.
        If Not Element.IsRoot And Attribute.Trait Is Nothing And
          Not Attribute.CategoriesString.Contains(CategoryNoCorrelation) Then

          ParentElement = Element.Parent

          ' Find siblings
          If ParentElement IsNot Nothing Then
            For Each SiblingElement In ParentElement.Elements
              If Not SiblingElement.UniqueID.Equals(Element.UniqueID) And
                SiblingElement.Template IsNot Nothing AndAlso
                SiblingElement.Template.UniqueID.Equals(
                Element.Template.UniqueID) Then ' Same template, skip self
                If SiblingElement.Attributes(Attribute.Parent.Name).
                  GetValue.IsGood Then ' Add only if has good data
                  CorrelationPoints.Add(New DBMCorrelationPoint(
                    New DBMPointDriver(SiblingElement.Attributes(
                    Attribute.Parent.Name)), False))
                End If
              End If
            Next
          End If

          ' Find parents recursively
          Do While ParentElement IsNot Nothing
            If ParentElement.Template IsNot Nothing AndAlso
              ParentElement.Template.UniqueID.Equals(
              Element.Template.UniqueID) Then ' Same template
              If ParentElement.Attributes(Attribute.Parent.Name).
                GetValue.IsGood Then ' Add only if has good data
                CorrelationPoints.Add(New DBMCorrelationPoint(
                  New DBMPointDriver(ParentElement.Attributes(
                  Attribute.Parent.Name)), True))
              End If
            End If
            ParentElement = ParentElement.Parent
          Loop

        End If

      End If

    End Sub


    Private Function DBMResult(Timestamp As AFTime,
      Optional EndTimestamp As AFTime = Nothing) As AFValue

      ' Return DBM result parameter based on applied property/trait.

      Dim Value As New AFValue

      Monitor.Enter(DBM) ' Request the lock, and block until it is obtained.
      Try

        If PointsStale.IsStale Then UpdatePoints ' Update points periodically
        If Not EndTimestamp.IsEmpty Then DBM.PrepareData(InputPointDriver,
          CorrelationPoints, Timestamp.LocalTime, EndTimestamp.LocalTime)

        With DBM.Result(
          InputPointDriver, CorrelationPoints, Timestamp.LocalTime)

          If Attribute.Trait Is Nothing Then
            Value = New AFValue(.Factor, .Timestamp)
            Value.Questionable = .HasEvent
            Value.Substituted = .HasSuppressedEvent
          ElseIf Attribute.Trait Is LimitTarget Then
            Value = New AFValue(.ForecastItem.Measurement, .Timestamp)
          ElseIf Attribute.Trait Is Forecast Then
            Value = New AFValue(.ForecastItem.ForecastValue, .Timestamp)
          ElseIf Attribute.Trait Is LimitMinimum Then
            Value = New AFValue(.ForecastItem.ForecastValue-
              .ForecastItem.Range(pValueMinMax), .Timestamp)
          ElseIf Attribute.Trait Is LimitLoLo Then
            Value = New AFValue(.ForecastItem.LowerControlLimit, .Timestamp)
          ElseIf Attribute.Trait Is LimitLo Then
            Value = New AFValue(.ForecastItem.ForecastValue-
              .ForecastItem.Range(pValueLoHi), .Timestamp)
          ElseIf Attribute.Trait Is LimitHi Then
            Value = New AFValue(.ForecastItem.ForecastValue+
              .ForecastItem.Range(pValueLoHi), .Timestamp)
          ElseIf Attribute.Trait Is LimitHiHi Then
            Value = New AFValue(.ForecastItem.UpperControlLimit, .Timestamp)
          ElseIf Attribute.Trait Is LimitMaximum Then
            Value = New AFValue(.ForecastItem.ForecastValue+
              .ForecastItem.Range(pValueMinMax), .Timestamp)
          End If

        End With

        Return Value

      Finally
        Monitor.Exit(DBM) ' Ensure that the lock is released.
      End Try

    End Function


    Public Overrides Function GetValue(context As Object,
      timeContext As Object, inputAttributes As AFAttributeList,
      inputValues As AFValues) As AFValue

      Dim Timestamp As AFTime

      If timeContext Is Nothing Then
        Timestamp = Now
      Else
        Timestamp = DirectCast(timeContext, AFTime)
      End If

      Return DBMResult(Timestamp)

    End Function


    Public Overrides Function GetValues(context As Object,
      timeContext As AFTimeRange, numberOfValues As Integer,
      inputAttributes As AFAttributeList, inputValues As AFValues()) As AFValues

      ' Returns values for each interval in a time range. The (aligned) end time
      ' itself is excluded.

      Dim IntervalSeconds As Double

      GetValues = New AFValues
      IntervalSeconds = Max(1, ((timeContext.EndTime.UtcSeconds-timeContext.
        StartTime.UtcSeconds)/CalculationInterval-1)/(numberOfValues-1))*
        CalculationInterval ' Required interval, first and last interv inclusive
      Do While timeContext.EndTime > timeContext.StartTime
        GetValues.Add(DBMResult(timeContext.StartTime, timeContext.EndTime))
        timeContext.StartTime = New AFTime(
          timeContext.StartTime.UtcSeconds+IntervalSeconds)
      Loop

      Return GetValues

    End Function


    Public Overrides Function RecordedValues(timeRange As AFTimeRange,
      boundaryType As AFBoundaryType, filterExpression As String,
      includeFilteredValues As Boolean, inputAttributes As AFAttributeList,
      inputValues As AFValues(), inputTimes As List(Of AFTime),
      Optional maxCount As Integer = 0) As AFValues

      Return GetValues(Nothing, timeRange, maxCount, Nothing, Nothing)

    End Function


  End Class


End Namespace
