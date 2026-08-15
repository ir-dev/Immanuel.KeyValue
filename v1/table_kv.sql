USE [immanuel_kv]
GO

/****** Object:  Table [immanuel_sa].[KeyVal]    Script Date: 7/17/2017 4:11:53 PM ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

--DROP TABLE [immanuel_sa].[KeyVal]
--GO

CREATE TABLE [immanuel_sa].[KeyVal](
	[Id] [bigint] IDENTITY(1,1) NOT NULL,
	[ClientKey] [varchar](8) NOT NULL,
	[KeyName] [varchar](64) NOT NULL,
	[KeyVal] [varchar](1024) NULL,
	[IpAddr] [varchar](64) NULL,
	[Agent] [varchar](128) NULL,
 CONSTRAINT [PK_KeyVal] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] 
GO

--alter table [immanuel_sa].[KeyVal] add CreatedAt datetime2 not null default getdate()

-- Unique Idx

CREATE UNIQUE NONCLUSTERED INDEX IX_ClientKey_KeyName
   ON [KeyVal] ([ClientKey], [KeyName])

GO



---------- merge update value

[sp_UpdateKeyVal] '1212', '1212', '121212', '12121', '121212'

DROP PROCEDURE [sp_UpdateKeyVal]
GO

CREATE PROCEDURE [sp_UpdateKeyVal]
(
	@ip_ClientKey varchar(8),
	@ip_KeyName varchar(64),
	@ip_KeyVal varchar(1024) = null,
	@ip_IpAddr varchar(64),
	@ip_Agent varchar(128)
)
AS
BEGIN
	MERGE [KeyVal] AS tgt
		using (select @ip_ClientKey ClientKey,
					  @ip_KeyName KeyName,
					  @ip_KeyVal KeyVal,
					  @ip_IpAddr IpAddr,
					  @ip_Agent Agent) as src
			on tgt.[ClientKey] = src.[ClientKey] and 
			   tgt.[KeyName] = src.[KeyName]
	when MATCHED then
		Update set tgt.[KeyVal] = @ip_KeyVal
	when not matched by target then
		INSERT ([ClientKey]
           ,[KeyName]
           ,[KeyVal]
           ,[IpAddr]
           ,[Agent])
		 VALUES
			   (@ip_ClientKey
			   ,@ip_KeyName
			   ,@ip_KeyVal
			   ,@ip_IpAddr
			   ,@ip_Agent)
			;
		
END

GO

DROP PROCEDURE [sp_SelectKeyVal]
GO

CREATE PROCEDURE [sp_SelectKeyVal]
(
	@ip_ClientKey varchar(8),
	@ip_KeyName varchar(64)
)
AS
BEGIN
	select KeyVal from [immanuel_sa].[KeyVal] where
				[ClientKey] = @ip_ClientKey and
				[KeyName] = @ip_KeyName
END
go


--[sp_UpdateAction] 'piiofpr9', 'VisitCnt', 'increment'

DROP PROCEDURE [sp_UpdateAction]
GO

CREATE PROCEDURE [sp_UpdateAction]
(
	@ip_ClientKey varchar(8),
	@ip_KeyName varchar(64),
	@ip_Action varchar(50)
)
AS
BEGIN
	if (upper(@ip_Action) = 'INCREMENT')
	begin
		update [immanuel_sa].[KeyVal] 
			set [KeyVal] = (ISNULL([KeyVal], 0) + 1)
		where ISNUMERIC(ISNULL([KeyVal], 0)) = 1
			and [ClientKey] = @ip_ClientKey 
			and [KeyName] = @ip_KeyName
	end
END
go