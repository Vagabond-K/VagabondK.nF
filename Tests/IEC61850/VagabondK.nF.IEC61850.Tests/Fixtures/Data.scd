<?xml version="1.0" encoding="utf-8"?>
<SCL xmlns="http://www.iec.ch/61850/2003/SCL" version="2007" revision="B">
  <IED name="NFTEST">
    <AccessPoint name="AP1">
      <Server>
        <LDevice inst="LD0">
          <LN0 lnClass="LLN0" inst="" lnType="Zero" />
          <LN lnClass="GGIO" inst="1" lnType="Measurements">
            <DOI name="Status"><DAI name="stVal"><Val>true</Val></DAI></DOI>
            <DOI name="Position"><DAI name="stVal"><Val>on</Val></DAI></DOI>
            <DOI name="AnalogControl"><DAI name="ctlModel"><Val>sbo-enhanced</Val></DAI></DOI>
            <DataSet name="Measurements">
              <FCDA ldInst="LD0" lnClass="GGIO" lnInst="1" doName="Status" daName="stVal" fc="ST" />
              <FCDA ldInst="LD0" lnClass="GGIO" lnInst="1" doName="Count" daName="stVal" fc="ST" />
              <FCDA ldInst="LD0" lnClass="GGIO" lnInst="1" doName="Analog" daName="mag.f" fc="MX" />
            </DataSet>
            <ReportControl name="Events" rptID="NFREPORT" datSet="Measurements" confRev="1"
                           buffered="false" indexed="false" bufTime="0" intgPd="0">
              <TrgOps dchg="true" qchg="true" period="true" gi="true" dupd="false" />
              <OptFields seqNum="true" dataSet="true" reasonCode="true" configRef="true" />
              <RptEnabled max="1" />
            </ReportControl>
          </LN>
        </LDevice>
      </Server>
    </AccessPoint>
  </IED>
  <DataTypeTemplates>
    <LNodeType id="Zero" lnClass="LLN0" />
    <LNodeType id="Measurements" lnClass="GGIO">
      <DO name="Status" type="Status" />
      <DO name="Count" type="Count" />
      <DO name="Analog" type="Analog" />
      <DO name="Position" type="Position" />
      <DO name="Mode" type="Mode" />
      <DO name="PositionControl" type="PositionControl" />
      <DO name="PositionSelected" type="PositionSelected" />
      <DO name="PositionEnhanced" type="PositionEnhanced" />
      <DO name="AnalogControl" type="AnalogControl" />
    </LNodeType>
    <DOType id="Status" cdc="SPS">
      <DA name="stVal" bType="BOOLEAN" fc="ST" dchg="true" />
      <DA name="q" bType="Quality" fc="ST" qchg="true" />
      <DA name="t" bType="Timestamp" fc="ST" />
    </DOType>
    <DOType id="Count" cdc="INS">
      <DA name="stVal" bType="INT32" fc="ST" dchg="true"><Val>-123456</Val></DA>
    </DOType>
    <DOType id="Analog" cdc="MV">
      <DA name="mag" bType="Struct" type="Vector" fc="MX" dchg="true" />
    </DOType>
    <DOType id="Position" cdc="DPS"><DA name="stVal" bType="Dbpos" fc="ST" dchg="true" /></DOType>
    <DOType id="Mode" cdc="ENS"><DA name="stVal" bType="Enum" type="OperatingMode" fc="ST" dchg="true"><Val>automatic</Val></DA></DOType>
    <DOType id="PositionControl" cdc="DPC">
      <DA name="stVal" bType="Dbpos" fc="ST" dchg="true" />
      <DA name="ctlModel" bType="Enum" type="ControlModel" fc="CF"><Val>direct-normal</Val></DA>
      <DA name="Oper" bType="Struct" type="BooleanOperate" fc="CO" />
      <DA name="Cancel" bType="Struct" type="BooleanCancel" fc="CO" />
    </DOType>
    <DOType id="AnalogControl" cdc="APC">
      <DA name="mxVal" bType="Struct" type="ControlAnalog" fc="MX" dchg="true" />
      <DA name="ctlModel" bType="Enum" type="ControlModel" fc="CF"><Val>direct-enhanced</Val></DA>
      <DA name="sboTimeout" bType="INT32U" fc="CF"><Val>2000</Val></DA>
      <DA name="sboClass" bType="Enum" type="SboClass" fc="CF"><Val>operate-once</Val></DA>
      <DA name="SBOw" bType="Struct" type="AnalogOperate" fc="CO" />
      <DA name="Oper" bType="Struct" type="AnalogOperate" fc="CO" />
      <DA name="Cancel" bType="Struct" type="AnalogCancel" fc="CO" />
    </DOType>
    <DOType id="PositionSelected" cdc="DPC">
      <DA name="stVal" bType="Dbpos" fc="ST" dchg="true" />
      <DA name="ctlModel" bType="Enum" type="ControlModel" fc="CF"><Val>sbo-normal</Val></DA>
      <DA name="SBO" bType="VisString129" fc="CO" />
      <DA name="Oper" bType="Struct" type="BooleanOperate" fc="CO" />
      <DA name="Cancel" bType="Struct" type="BooleanCancel" fc="CO" />
    </DOType>
    <DOType id="PositionEnhanced" cdc="DPC">
      <DA name="stVal" bType="Dbpos" fc="ST" dchg="true" />
      <DA name="ctlModel" bType="Enum" type="ControlModel" fc="CF"><Val>direct-enhanced</Val></DA>
      <DA name="Oper" bType="Struct" type="BooleanOperate" fc="CO" />
      <DA name="Cancel" bType="Struct" type="BooleanCancel" fc="CO" />
    </DOType>
    <DAType id="Vector"><BDA name="f" bType="FLOAT32"><Val>12.5</Val></BDA></DAType>
    <DAType id="ControlAnalog"><BDA name="f" bType="FLOAT32" /></DAType>
    <DAType id="Origin"><BDA name="orCat" bType="Enum" type="OriginCategory" /><BDA name="orIdent" bType="Octet64" /></DAType>
    <DAType id="BooleanOperate">
      <BDA name="ctlVal" bType="BOOLEAN" /><BDA name="origin" bType="Struct" type="Origin" />
      <BDA name="ctlNum" bType="INT8U" /><BDA name="T" bType="Timestamp" /><BDA name="Test" bType="BOOLEAN" /><BDA name="Check" bType="Check" />
    </DAType>
    <DAType id="BooleanCancel">
      <BDA name="ctlVal" bType="BOOLEAN" /><BDA name="origin" bType="Struct" type="Origin" />
      <BDA name="ctlNum" bType="INT8U" /><BDA name="T" bType="Timestamp" /><BDA name="Test" bType="BOOLEAN" />
    </DAType>
    <DAType id="AnalogOperate">
      <BDA name="ctlVal" bType="Struct" type="ControlAnalog" /><BDA name="origin" bType="Struct" type="Origin" />
      <BDA name="ctlNum" bType="INT8U" /><BDA name="T" bType="Timestamp" /><BDA name="Test" bType="BOOLEAN" /><BDA name="Check" bType="Check" />
    </DAType>
    <DAType id="AnalogCancel">
      <BDA name="ctlVal" bType="Struct" type="ControlAnalog" /><BDA name="origin" bType="Struct" type="Origin" />
      <BDA name="ctlNum" bType="INT8U" /><BDA name="T" bType="Timestamp" /><BDA name="Test" bType="BOOLEAN" />
    </DAType>
    <EnumType id="OperatingMode"><EnumVal ord="-1">unknown</EnumVal><EnumVal ord="0">manual</EnumVal><EnumVal ord="2">automatic</EnumVal></EnumType>
    <EnumType id="ControlModel">
      <EnumVal ord="0">status-only</EnumVal><EnumVal ord="1">direct-normal</EnumVal><EnumVal ord="2">sbo-normal</EnumVal>
      <EnumVal ord="3">direct-enhanced</EnumVal><EnumVal ord="4">sbo-enhanced</EnumVal>
    </EnumType>
    <EnumType id="SboClass"><EnumVal ord="0">operate-once</EnumVal><EnumVal ord="1">operate-many</EnumVal></EnumType>
    <EnumType id="OriginCategory"><EnumVal ord="0">not-supported</EnumVal><EnumVal ord="3">remote-control</EnumVal></EnumType>
  </DataTypeTemplates>
</SCL>
