<?xml version="1.0" encoding="utf-8"?>
<!--
  HarvestTransform.xslt
  Applied to the WiX v4 HarvestDirectory output before compilation.

  Purpose: suppress the DnsGoBetween.Api.exe component from the auto-harvest
  so that Service.wxs can own it (and add ServiceInstall/ServiceControl to it).

  Without this, the harvester would include the EXE in HarvestedFiles AND
  Service.wxs would reference it again, causing a duplicate Component error.
-->
<xsl:stylesheet version="1.0"
  xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
  xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
  exclude-result-prefixes="xsl wix">

  <xsl:output method="xml" indent="yes" omit-xml-declaration="yes" />

  <!-- Identity transform: pass everything through unchanged by default. -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!--
    Suppress any Component whose child File/@Source contains the service EXE name.
    The case-insensitive translate() handles mixed-case publish paths on Windows.
  -->
  <xsl:template
    match="wix:Component[
      wix:File[
        contains(
          translate(@Source,
            'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
            'abcdefghijklmnopqrstuvwxyz'),
          'dnsgobetween.api.exe')
      ]
    ]" />

  <!--
    Also drop any Directory element that becomes empty after the above suppression
    (WiX will error on empty harvested directories).
  -->
  <xsl:template
    match="wix:Directory[not(descendant::wix:Component)]" />

</xsl:stylesheet>
