/****** Script for SelectTopNRows command from SSMS  ******/
SELECT TOP 1000 [Id]
      ,[ConferenceRecordId]
      ,[Message]
      ,[LogTime]
  FROM [dbo].[LogMessages]

  delete [LogMessages]