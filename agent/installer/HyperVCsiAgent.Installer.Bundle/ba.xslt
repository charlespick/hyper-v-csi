<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet
  version="1.0"
  xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
  xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
  exclude-result-prefixes="wix"
  >

  <xsl:output method="xml" indent="yes"/>

  <xsl:template match="@* | node()">
    <xsl:copy>
      <xsl:apply-templates select="@* | node()"/>
    </xsl:copy>
  </xsl:template>

  <!-- Already referenced directly via BootstrapperApplication/@SourceFile in
       Bundle.wxs - harvesting it a second time here would author the same
       file as two different payloads. -->
  <xsl:template match="wix:Payload[@SourceFile='SourceDir\HyperVCsiAgent.Installer.Bootstrapper.exe']" />
</xsl:stylesheet>
