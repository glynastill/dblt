dblt
====

A C# load testing program for PostgreSQL using the npgsql data provider.

Usage
-----

Run parameters are specified in the *App.config* file and the sql transactions
to run are read from a separate xml file specified in the *TransactionsFile*
parameter.

App.config parameters
---------------------

**ServerDescription** (text) 		 - A description about the test run

**PgConnectionString** (test)		 - The npgsql connection string

**ConnectionRetry** (boolean)		 - boolean - Whether or not clients that
					   fail to connect should retry (true), 
					   or give up (false)
					   
**ConnectionPerTransaction** (boolean)	 - boolean - Whether clients should 
					   connect and disconnect for each 
					   attempted transaction (true), or keep
					   their connection open (false)
					   
**LogFile** (text)			 - Path to file where verbose details 
					   of the test run are logged
					   
**CsvLogFile** (text)			 - Path to file where statistical results
					   of test run are logged
					   
**LogLevel** (integer)			 - Verbosity level of log data logged into
					   the log file specified in the "LogFile" 
					   parameter 
					   (0 = minimal, 1 = standard, 2 = verbose)
					   
**VerboseScreen** (boolean)		 - Display verbose details of the test 
					   to the screen rather than show a summary
					   
**Clients** (integer)			 - Quantity of clients to use for a single
					   test, set to 0 if doing multiple tests.
					   
**ClientsScale** (integer)		 - Set along with "ClientsMax" parameter 
					   to run a set of tests with incrementing
					   clients
					   E.g. ClientsScale = 4 ClientsMax = 16
					   will run 4 tests with 4,8,12 and 16 clients
					   
**ClientsMax** (integer) 		- Maximum quantiy of clients to run the test 
					  with as detailed above
					  
**Iterations** (integer)		- Number of iterations for each client 
					  to run
					  
**SleepTime** (integer)			- Time for each client to sleep between 
					  iterations (milliseconds)
					  
**TransactionsFile** (text)		- Xml file containing the SQL for each  
					  iteration of the client - this can contain
					  multiple transactions.

The transactions xml file contains a set of transactions and sql statements to 
be executed against the database.  There's nothing overly intelligent about this
file, it is simply a <transactions> root node containing *\<transaction>* and *\<sql>* 
sub nodes, for which any can have a random attribute assigned to make them be 
either run or skipped on a random basis. 

The text *#client_id#* will also be replaced with the integer id of each client 
thread in the testing program; this can be usefull for identifying individual
clients against queries.
 
    <transactions>
       <transaction random="true">
         <sql>SELECT 'I am client #client_id#';</sql>
         <sql>SELECT 2;</sql>
       </transaction>
       <transaction>
         <sql random="true">SELECT 'something';</sql>
         <sql>SELECT 20;</sql>
       </transaction>
    </transactions>


