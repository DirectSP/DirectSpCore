﻿CREATE FUNCTION [dsp].[Formatter_FormatString] (@str TSTRING)
RETURNS TSTRING
AS
BEGIN
	RETURN NULLIF(TRIM(@str), '');
END;