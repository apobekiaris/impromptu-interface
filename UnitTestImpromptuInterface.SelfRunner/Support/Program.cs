﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UnitTestImpromptuInterface
{
    class Program
    {
        static void Main(string[] args)
        {
			var hashset = new HashSet<string>(args);
			var tSuccess=0;
			var tFailed =0;
            var tTypes =
                Assembly.GetAssembly(typeof (Program)).GetTypes()
                    .Where(it => it.GetCustomAttributes(typeof (TestFixtureAttribute), false).Any());

			Console.WriteLine("Press a key to start.");
			Console.Read();
            foreach (var tType in tTypes)
            {
                Console.WriteLine(tType.Name);
                var tMethods =
                    tType.GetMethods().Where(it => it.GetCustomAttributes(typeof (TestAttribute), false).Any());
                foreach (var tMethod in tMethods)
                {
                    var tObj = Activator.CreateInstance(tType);
                  	if(hashset.Any() && !hashset.Contains(String.Format("{0}.{1}",tType.Name,tMethod.Name))){
						continue;
					}
					
                    Console.Write("    ");
                    Console.WriteLine(tMethod.Name);
                    try
                    {
                        tMethod.Invoke(tObj,null);
                        Console.Write("       ");
                        Console.WriteLine("Success");
						tSuccess++;
                    }
                    catch (TargetInvocationException ex)
                    {
                        Console.Write("*      ");
                        if (ex.InnerException is AssertionException)
                        {

                            Console.Write("Failed: ");
                            Console.WriteLine(ex.InnerException.Message);
                            Console.WriteLine();
							tFailed++;

                        }
                        else
                        {
                            throw ex.InnerException;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Exception:");
                        Console.Write(ex);
                        Console.WriteLine();
                    }
                }


            }
			Console.WriteLine("Done. Successes:{0} Failures:{1}",tSuccess,tFailed);
			Console.Read();
        }

       
    }
}